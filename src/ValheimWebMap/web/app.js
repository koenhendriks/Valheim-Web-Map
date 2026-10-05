(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };
  var el = {
    world: $('world-name'), clock: $('clock'), render: $('render'), renderText: $('render-text'),
    renderBar: $('render-bar'), panel: $('panel'), toggle: $('panel-toggle'), count: $('panel-count'),
    players: $('players'), noPlayers: $('no-players'), explored: $('explored'), coords: $('coords'),
    history: $('history'), noHistory: $('no-history')
  };
  var activeTab = 'online';
  var expanded = {};

  // Cartography table markers and death markers.
  var pins = { owners: [], pins: [], deaths: [] };
  var lastPinsVersion = -1;
  var pinMarkers = {};
  var markerPrefs = loadMarkerPrefs();

  var map, crs, HALF, info;
  var features = {};
  var tileLayer = null;
  var tileLayerKey = '';
  var pendingTileKey = '';
  var lastTileSwap = 0;
  var markers = {};
  var followId = null;
  var state = { players: [] };

  fetch('api/info').then(function (r) { return r.json(); }).then(init).catch(function (e) {
    el.world.textContent = 'Could not reach the map server';
    console.error(e);
  });

  function init(i) {
    info = i;
    features = i.features || {};
    HALF = i.mapHalfSize;
    var scale0 = i.tileSize / (2 * HALF);
    // Map units are world metres: L.latLng(z, x). North (+z) is up.
    crs = L.extend({}, L.CRS.Simple, {
      transformation: new L.Transformation(scale0, i.tileSize / 2, -scale0, i.tileSize / 2)
    });

    map = L.map('map', {
      crs: crs,
      minZoom: 1,
      maxZoom: i.maxZoom,
      zoomSnap: 0.25,
      zoomDelta: 0.5,
      wheelPxPerZoomLevel: 90,
      attributionControl: false,
      maxBounds: [[-HALF * 1.15, -HALF * 1.15], [HALF * 1.15, HALF * 1.15]],
      maxBoundsViscosity: 0.8
    });
    L.control.scale({ imperial: false, maxWidth: 160 }).addTo(map);
    map.createPane('pins').style.zIndex = 450; // below player markers (600)
    map.on('zoomend', updateLabelVisibility);
    updateLabelVisibility();

    if (!applyHash()) map.setView([0, 0], 2);

    map.on('moveend zoomend', updateHash);
    map.on('dragstart', function () { setFollow(null); });
    map.on('mousemove', function (e) {
      el.coords.textContent = 'x ' + Math.round(e.latlng.lng) + '  z ' + Math.round(e.latlng.lat);
    });
    map.on('mouseout', function () { el.coords.textContent = ''; });

    el.toggle.addEventListener('click', function () { el.panel.classList.toggle('open'); });
    Array.prototype.forEach.call(document.querySelectorAll('.tab'), function (btn) {
      btn.addEventListener('click', function () { showTab(btn.getAttribute('data-tab')); });
    });
    var showMarkersTab = features.pins !== false || features.deathMarkers !== false;
    if (features.history === false) document.querySelector('.tab[data-tab="history"]').style.display = 'none';
    if (!showMarkersTab) document.querySelector('.tab[data-tab="markers"]').style.display = 'none';
    if (features.history === false && !showMarkersTab) {
      document.querySelector('.tabs').style.display = 'none';
      $('tab-online').insertAdjacentHTML('afterbegin', '<h1 class="panel-title">Players</h1>');
    }
    if (features.history !== false) {
      pollHistory();
      setInterval(pollHistory, 15000);
    }
    setupMarkerFilters();
    window.addEventListener('hashchange', function () { if (!suppressHash) applyHash(); });

    poll();
    setInterval(poll, Math.max(1000, (i.updateInterval || 1) * 1000));
  }

  // --- tiles -------------------------------------------------------------

  function ensureTiles(s) {
    if (!s.mapReady) return;
    var key = s.epoch + ':' + s.mapId + ':' + s.exploreVersion;
    if (key === tileLayerKey || key === pendingTileKey) return;

    // The first layer goes up right away; later ones only when the terrain picture changed
    // (new resolution) or after a pause, so panning is not interrupted by constant reloads.
    var now = Date.now();
    var atlasChanged = !tileLayer || tileLayer.options.mapId !== s.mapId || tileLayer.options.epoch !== s.epoch;
    if (!atlasChanged && now - lastTileSwap < 15000) return;

    pendingTileKey = key;
    lastTileSwap = now;
    var layer = L.tileLayer('tiles/{z}/{x}/{y}.png?v={epoch}-{mapId}-{v}', {
      tileSize: info.tileSize,
      minZoom: 0,
      maxZoom: info.maxZoom,
      maxNativeZoom: s.nativeZoom,
      noWrap: true,
      keepBuffer: 3,
      updateWhenZooming: false,
      bounds: [[-HALF, -HALF], [HALF, HALF]],
      epoch: s.epoch,
      mapId: s.mapId,
      v: s.exploreVersion,
      className: 'map-tiles'
    });
    var old = tileLayer;
    var done = false;
    var finish = function () {
      if (done) return;
      done = true;
      if (old) map.removeLayer(old);
      tileLayer = layer;
      tileLayerKey = key;
      pendingTileKey = '';
      layer.bringToBack();
    };
    layer.once('load', finish);
    setTimeout(finish, 4000);
    layer.addTo(map);
  }

  // --- players -----------------------------------------------------------

  function poll() {
    fetch('api/state', { cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (s) {
      state = s;
      renderStatus(s);
      ensureTiles(s);
      renderPlayers(s.players || []);
      if ((features.pins !== false || features.deathMarkers !== false) && s.pinsVersion !== lastPinsVersion) {
        fetchPins(s.pinsVersion);
      }
    }).catch(function () {
      el.world.textContent = 'Connection lost…';
    });
  }

  function renderStatus(s) {
    el.world.textContent = s.world || 'Waiting for the world to load…';
    if (typeof s.day === 'number') {
      var mins = Math.floor((s.timeOfDay || 0) * 24 * 60);
      var hh = String(Math.floor(mins / 60)).padStart(2, '0');
      var mm = String(mins % 60).padStart(2, '0');
      el.clock.textContent = 'Day ' + s.day + ' · ' + hh + ':' + mm;
    } else {
      el.clock.textContent = '';
    }
    var rendering = s.world && (!s.mapReady || s.renderProgress < 1) && !s.renderError;
    el.render.classList.toggle('hidden', !rendering);
    if (rendering) {
      var pct = Math.round((s.renderProgress || 0) * 100);
      el.renderText.textContent = s.mapReady ? 'Sharpening map… ' + pct + '%' : 'Rendering world map… ' + pct + '%';
      el.renderBar.style.width = pct + '%';
    }
    if (s.renderError) {
      el.render.classList.remove('hidden');
      el.renderText.textContent = 'Map render failed, see server log';
      el.renderBar.style.width = '0%';
    }
    el.explored.textContent = typeof s.exploredPercent === 'number' ? s.exploredPercent.toFixed(1) + '% explored' : '';
  }

  function renderPlayers(players) {
    var seen = {};
    el.count.textContent = players.length;
    el.noPlayers.classList.toggle('hidden', players.length > 0);

    players.sort(function (a, b) {
      if (a.visible !== b.visible) return a.visible ? -1 : 1;
      return a.name.localeCompare(b.name);
    });

    var frag = document.createDocumentFragment();
    players.forEach(function (p) {
      var key = String(p.id) + ':' + p.name;
      seen[key] = true;
      var color = colorFor(p.name);
      var li = document.createElement('li');
      li.style.setProperty('--c', color);
      li.className = (p.visible ? '' : 'hidden-pos') + (key === followId ? ' following' : '');
      var parts = [];
      if (p.dead) parts.push('dead');
      else if (!p.visible) parts.push('position hidden');
      else {
        if (p.biome) parts.push(p.biome);
        if (features.coordinates !== false && typeof p.x === 'number') parts.push(Math.round(p.x) + ', ' + Math.round(p.z));
      }
      if (p.since) parts.push('online ' + duration((Date.now() - Date.parse(p.since)) / 1000));
      if (typeof p.deaths === 'number' && p.deaths > 0) parts.push(p.deaths + (p.deaths === 1 ? ' death' : ' deaths'));
      li.innerHTML = '<span class="dot"></span><span class="name"></span>' +
        '<span class="follow">' + (key === followId ? 'following' : p.dead ? '<span class="skull">☠</span>' : '') + '</span>' +
        '<span class="sub"></span>';
      li.querySelector('.name').textContent = p.name;
      li.querySelector('.sub').textContent = parts.join(' · ');
      if (p.visible) {
        li.addEventListener('click', function () {
          setFollow(key === followId ? null : key);
          map.flyTo([p.z, p.x], Math.max(map.getZoom(), 4), { duration: 0.8 });
          if (window.innerWidth <= 720) el.panel.classList.remove('open');
        });
      }
      frag.appendChild(li);

      if (p.visible) {
        updateMarker(key, p, color);
      } else if (markers[key]) {
        map.removeLayer(markers[key]);
        delete markers[key];
      }
    });
    el.players.innerHTML = '';
    el.players.appendChild(frag);

    Object.keys(markers).forEach(function (key) {
      if (!seen[key]) {
        map.removeLayer(markers[key]);
        delete markers[key];
      }
    });

    if (followId && !seen[followId]) setFollow(null);
    if (followId && markers[followId]) {
      map.panTo(markers[followId].getLatLng(), { animate: true, duration: 0.9 });
    }
  }

  function updateMarker(key, p, color) {
    var latlng = [p.z, p.x];
    var m = markers[key];
    if (!m) {
      var icon = L.divIcon({
        className: 'player-marker',
        iconSize: [0, 0],
        iconAnchor: [0, 0],
        html: '<div class="pm"><div class="pm-arrow"></div><div class="pm-dot"></div><div class="pm-label"></div></div>'
      });
      m = L.marker(latlng, { icon: icon, zIndexOffset: 1000, keyboard: false });
      m.on('click', function () { setFollow(key === followId ? null : key); });
      m.addTo(map);
      markers[key] = m;
    } else {
      m.setLatLng(latlng);
    }
    var root = m.getElement() && m.getElement().querySelector('.pm');
    if (root) {
      root.style.setProperty('--c', color);
      root.style.setProperty('--yaw', Math.round(p.yaw || 0) + 'deg');
      root.classList.toggle('no-heading', typeof p.yaw !== 'number');
      root.classList.toggle('following', key === followId);
      root.querySelector('.pm-label').textContent = p.name;
      root.title = p.name + (p.biome ? ' · ' + p.biome : '');
    }
  }

  // --- history -----------------------------------------------------------

  function showTab(name) {
    activeTab = name;
    Array.prototype.forEach.call(document.querySelectorAll('.tab'), function (b) {
      b.classList.toggle('active', b.getAttribute('data-tab') === name);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.tab-page'), function (pg) {
      pg.classList.toggle('active', pg.id === 'tab-' + name);
    });
    if (name === 'history') pollHistory();
  }

  function pollHistory() {
    if (activeTab !== 'history' && el.history.childElementCount > 0) return;
    fetch('api/history', { cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (h) {
      renderHistory(h.players || []);
    }).catch(function () {});
  }

  function renderHistory(players) {
    el.noHistory.classList.toggle('hidden', players.length > 0);
    var frag = document.createDocumentFragment();
    players.forEach(function (p) {
      var li = document.createElement('li');
      li.className = 'history';
      li.innerHTML = '<span class="name"></span><span class="online-dot" style="display:none"></span>' +
        '<span class="stats"></span>';
      li.querySelector('.name').textContent = p.name;
      if (p.online) li.querySelector('.online-dot').style.display = '';
      var stats = [];
      if (typeof p.sessions === 'number') stats.push(p.sessions + (p.sessions === 1 ? ' session' : ' sessions'));
      if (typeof p.playSeconds === 'number') stats.push(duration(p.playSeconds) + ' played');
      if (typeof p.deaths === 'number') stats.push(p.deaths + (p.deaths === 1 ? ' death' : ' deaths'));
      if (p.online) stats.push('online now');
      else if (p.lastSeen) stats.push('last seen ' + relative(p.lastSeen));
      li.querySelector('.stats').textContent = stats.join(' · ');
      if (expanded[p.name] && p.recent && p.recent.length) {
        var ul = document.createElement('ul');
        ul.className = 'sessions';
        p.recent.forEach(function (s) {
          var row = document.createElement('li');
          row.innerHTML = '<span class="date"></span><span class="dur"></span><span class="d"></span>';
          row.querySelector('.date').textContent = formatDate(s.start) + (s.character !== p.name ? ' (' + s.character + ')' : '');
          row.querySelector('.dur').textContent = typeof s.seconds === 'number' ? duration(s.seconds) : '';
          row.querySelector('.d').textContent = s.deaths ? '☠ ' + s.deaths : '';
          ul.appendChild(row);
        });
        li.appendChild(ul);
      }
      li.addEventListener('click', function () {
        expanded[p.name] = !expanded[p.name];
        renderHistory(players);
      });
      frag.appendChild(li);
    });
    el.history.innerHTML = '';
    el.history.appendChild(frag);
  }

  function duration(seconds) {
    seconds = Math.max(0, Math.floor(seconds || 0));
    var h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60);
    if (h >= 24) { var d = Math.floor(h / 24); return d + 'd ' + (h % 24) + 'h'; }
    if (h > 0) return h + 'h ' + m + 'm';
    if (m > 0) return m + 'm';
    return seconds + 's';
  }

  function relative(iso) {
    var s = (Date.now() - Date.parse(iso)) / 1000;
    if (s < 60) return 'just now';
    if (s < 3600) return Math.floor(s / 60) + ' min ago';
    if (s < 86400) return Math.floor(s / 3600) + ' h ago';
    var d = Math.floor(s / 86400);
    return d === 1 ? 'yesterday' : d + ' days ago';
  }

  function formatDate(iso) {
    var d = new Date(iso);
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) + ' ' +
      d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  }

  // --- cartography table markers ---------------------------------------------

  function loadMarkerPrefs() {
    var prefs = { hiddenOwners: {}, pins: true, checked: true, deaths: true };
    try {
      var saved = JSON.parse(localStorage.getItem('vwm.markers') || '{}');
      if (saved && typeof saved === 'object') {
        if (saved.hiddenOwners && typeof saved.hiddenOwners === 'object') prefs.hiddenOwners = saved.hiddenOwners;
        ['pins', 'checked', 'deaths'].forEach(function (k) { if (typeof saved[k] === 'boolean') prefs[k] = saved[k]; });
      }
    } catch (e) { /* storage unavailable: defaults */ }
    return prefs;
  }

  function saveMarkerPrefs() {
    try { localStorage.setItem('vwm.markers', JSON.stringify(markerPrefs)); } catch (e) { /* ignore */ }
  }

  function setupMarkerFilters() {
    var map_ = { 'f-pins': 'pins', 'f-checked': 'checked', 'f-deaths': 'deaths' };
    Object.keys(map_).forEach(function (id) {
      var box = $(id);
      box.checked = markerPrefs[map_[id]];
      box.addEventListener('change', function () {
        markerPrefs[map_[id]] = box.checked;
        saveMarkerPrefs();
        renderPinMarkers();
      });
    });
    if (features.pins === false) { $('f-pins').parentNode.classList.add('hidden'); $('f-checked-wrap').classList.add('hidden'); }
    if (features.checkedPins === false) $('f-checked-wrap').classList.add('hidden');
    if (features.deathMarkers === false) $('f-deaths-wrap').classList.add('hidden');
  }

  function fetchPins(version) {
    fetch('api/pins', { cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (p) {
      pins = { owners: p.owners || [], pins: p.pins || [], deaths: p.deaths || [] };
      lastPinsVersion = version;
      renderOwners();
      renderPinMarkers();
    }).catch(function () {});
  }

  function ownerLabel(o) { return o.name || 'Unknown owner'; }

  function renderOwners() {
    var list = $('pin-owners');
    var owners = pins.owners.slice().sort(function (a, b) {
      if (a.online !== b.online) return a.online ? -1 : 1;
      if (!!a.name !== !!b.name) return a.name ? -1 : 1;
      return b.pins - a.pins;
    });
    $('no-pins').classList.toggle('hidden', owners.length > 0 || pins.deaths.length > 0);
    var frag = document.createDocumentFragment();
    owners.forEach(function (o) {
      var li = document.createElement('li');
      li.className = 'owner' + (o.name ? '' : ' unknown');
      li.style.setProperty('--c', colorFor(o.name || 'owner:' + o.id));
      li.innerHTML = '<input type="checkbox"><span class="dot"></span><span class="name"></span><span class="count"></span>';
      var box = li.querySelector('input');
      box.checked = !markerPrefs.hiddenOwners[o.id];
      li.querySelector('.name').textContent = ownerLabel(o);
      if (o.online) li.querySelector('.name').insertAdjacentHTML('beforeend', '<span class="online-dot" title="online"></span>');
      li.querySelector('.count').textContent = o.pins + (o.pins === 1 ? ' marker' : ' markers');
      var toggle = function (checked) {
        if (checked) delete markerPrefs.hiddenOwners[o.id]; else markerPrefs.hiddenOwners[o.id] = true;
        box.checked = checked;
        saveMarkerPrefs();
        renderPinMarkers();
      };
      box.addEventListener('click', function (e) { e.stopPropagation(); toggle(box.checked); });
      li.addEventListener('click', function () { toggle(!box.checked); });
      frag.appendChild(li);
    });
    list.innerHTML = '';
    list.appendChild(frag);
  }

  var bossNames = {
    '$enemy_eikthyr': 'Eikthyr', '$enemy_gdking': 'The Elder', '$enemy_bonemass': 'Bonemass', '$enemy_dragon': 'Moder',
    '$enemy_goblinking': 'Yagluth', '$enemy_seekerqueen': 'The Queen', '$enemy_fader': 'Fader'
  };
  function prettyName(name) {
    if (!name) return '';
    if (bossNames[name]) return bossNames[name];
    if (name.charAt(0) === '$') {
      return name.replace(/^\$[a-z]+_/, '').replace(/_/g, ' ').replace(/\b\w/g, function (c) { return c.toUpperCase(); });
    }
    return name;
  }

  function renderPinMarkers() {
    var wanted = {};
    if (features.pins !== false && markerPrefs.pins) {
      pins.pins.forEach(function (p) {
        if (markerPrefs.hiddenOwners[p.owner]) return;
        if (p.checked && !markerPrefs.checked) return;
        wanted[p.id] = { x: p.x, z: p.z, cls: 'pin-' + p.type + (p.checked ? ' checked' : ''), label: prettyName(p.name), title: ownerOf(p.owner) + ' · ' + p.type };
      });
    }
    if (features.deathMarkers !== false && markerPrefs.deaths) {
      pins.deaths.forEach(function (d) {
        var id = 'death:' + d.name + ':' + d.time;
        wanted[id] = { x: d.x, z: d.z, cls: 'pin-death', label: d.name + ' · Day ' + d.day, title: 'Died ' + formatDate(d.time) };
      });
    }
    Object.keys(wanted).forEach(function (id) {
      var w = wanted[id];
      var m = pinMarkers[id];
      if (!m) {
        m = L.marker([w.z, w.x], {
          pane: 'pins', keyboard: false, interactive: true,
          icon: L.divIcon({ className: 'pin-marker', iconSize: [0, 0], iconAnchor: [0, 0], html: '<div class="pin"><span class="pin-glyph"></span><span class="pin-label"></span></div>' })
        }).addTo(map);
        pinMarkers[id] = m;
      }
      var root = m.getElement() && m.getElement().querySelector('.pin');
      if (root) {
        root.className = 'pin ' + w.cls;
        root.querySelector('.pin-label').textContent = w.label;
        root.title = (w.label ? w.label + ' · ' : '') + w.title;
      }
    });
    Object.keys(pinMarkers).forEach(function (id) {
      if (!wanted[id]) { map.removeLayer(pinMarkers[id]); delete pinMarkers[id]; }
    });
  }

  function ownerOf(id) {
    for (var i = 0; i < pins.owners.length; i++) if (pins.owners[i].id === id) return ownerLabel(pins.owners[i]);
    return 'Unknown owner';
  }

  function updateLabelVisibility() {
    $('map').classList.toggle('labels-off', map.getZoom() < 3);
  }

  function setFollow(key) {
    followId = key;
    renderPlayers(state.players || []);
  }

  function colorFor(name) {
    var h = 0;
    for (var i = 0; i < name.length; i++) h = (h * 31 + name.charCodeAt(i)) >>> 0;
    return 'hsl(' + (h % 360) + ', 70%, 60%)';
  }

  // --- URL hash: #x,z,zoom -------------------------------------------------

  var suppressHash = false;
  function updateHash() {
    var c = map.getCenter();
    suppressHash = true;
    history.replaceState(null, '', '#' + Math.round(c.lng) + ',' + Math.round(c.lat) + ',' + map.getZoom().toFixed(2));
    setTimeout(function () { suppressHash = false; }, 0);
  }

  function applyHash() {
    var m = /^#(-?\d+),(-?\d+),(\d+(?:\.\d+)?)$/.exec(location.hash);
    if (!m) return false;
    map.setView([+m[2], +m[1]], +m[3]);
    return true;
  }
})();
