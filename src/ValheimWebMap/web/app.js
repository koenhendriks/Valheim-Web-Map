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

  var map, crs, HALF, info;
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
    pollHistory();
    setInterval(pollHistory, 15000);
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
      var where = p.dead ? 'dead' : p.visible ? (p.biome || '') + ' · ' + Math.round(p.x) + ', ' + Math.round(p.z) : 'position hidden';
      var extra = [];
      if (p.since) extra.push('online ' + duration((Date.now() - Date.parse(p.since)) / 1000));
      if (typeof p.deaths === 'number' && p.deaths > 0) extra.push(p.deaths + (p.deaths === 1 ? ' death' : ' deaths'));
      li.innerHTML = '<span class="dot"></span><span class="name"></span>' +
        '<span class="follow">' + (key === followId ? 'following' : p.dead ? '<span class="skull">☠</span>' : '') + '</span>' +
        '<span class="sub"></span>';
      li.querySelector('.name').textContent = p.name;
      li.querySelector('.sub').textContent = where + (extra.length ? ' · ' + extra.join(' · ') : '');
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
      var when = p.online ? 'online now' : p.lastSeen ? 'last seen ' + relative(p.lastSeen) : '';
      li.querySelector('.stats').textContent =
        p.sessions + (p.sessions === 1 ? ' session' : ' sessions') + ' · ' + duration(p.playSeconds) + ' played · ' +
        p.deaths + (p.deaths === 1 ? ' death' : ' deaths') + (when ? ' · ' + when : '');
      if (expanded[p.name] && p.recent && p.recent.length) {
        var ul = document.createElement('ul');
        ul.className = 'sessions';
        p.recent.forEach(function (s) {
          var row = document.createElement('li');
          row.innerHTML = '<span class="date"></span><span class="dur"></span><span class="d"></span>';
          row.querySelector('.date').textContent = formatDate(s.start) + (s.character !== p.name ? ' (' + s.character + ')' : '');
          row.querySelector('.dur').textContent = duration(s.seconds);
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
