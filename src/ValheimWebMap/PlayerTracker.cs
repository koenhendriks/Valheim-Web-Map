using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimWebMap
{
    internal struct PlayerEntry
    {
        /// <summary>Connection id; changes every time the player connects.</summary>
        public long Id;
        /// <summary>Stable identity across connections: platform user id when known, else the character name.</summary>
        public string Key;
        public string Name;
        /// <summary>The player's in-game "visible to other players" map setting, and a spawned character to show.</summary>
        public bool Visible;
        public bool HasCharacter;
        /// <summary>Identifies the spawned character (ZDOID as text); empty without one. A new value while connected means a respawn.</summary>
        public string CharacterKey;
        public bool Dead;
        public Vector3 Position;
        public float Yaw;
        public string Biome;
    }

    /// <summary>Reads connected players from ZNet. Main thread only.</summary>
    internal static class PlayerTracker
    {
        public const string HostKey = "host";

        // Only the server's own (non-dedicated host) player keeps this flag on ZNet rather than on a peer.
        private static readonly FieldInfo HostPublicRefPos =
            typeof(ZNet).GetField("m_publicReferencePosition", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Dictionary<long, string> KeyByPeer = new Dictionary<long, string>();

        public static void Collect(ZNet znet, List<PlayerEntry> into)
        {
            into.Clear();
            ZDOMan zdoMan = ZDOMan.instance;
            WorldGenerator wg = WorldGenerator.instance;

            foreach (ZNetPeer peer in znet.GetPeers())
            {
                if (peer == null || !peer.IsReady() || peer.m_server || string.IsNullOrEmpty(peer.m_playerName)) continue;

                bool hasCharacter = !peer.m_characterID.IsNone();
                ZDO zdo = hasCharacter && zdoMan != null ? zdoMan.GetZDO(peer.m_characterID) : null;
                var entry = new PlayerEntry
                {
                    Id = peer.m_uid,
                    Key = KeyFor(peer),
                    Name = peer.m_playerName,
                    HasCharacter = hasCharacter,
                    CharacterKey = hasCharacter ? peer.m_characterID.ToString() : "",
                    Dead = zdo != null && zdo.GetBool(ZDOVars.s_dead, false),
                    Visible = hasCharacter && peer.m_publicRefPos,
                    Position = zdo != null ? zdo.GetPosition() : peer.m_refPos,
                    Yaw = zdo != null ? zdo.GetRotation().eulerAngles.y : 0f,
                };
                entry.Biome = wg != null && hasCharacter ? BiomeId.Name(wg.GetBiome(entry.Position.x, entry.Position.z)) : "";
                into.Add(entry);
            }

            Player host = Player.m_localPlayer;
            if (host != null)
            {
                Vector3 pos = host.transform.position;
                bool visible = HostPublicRefPos != null && (bool)HostPublicRefPos.GetValue(znet);
                into.Add(new PlayerEntry
                {
                    Id = 0,
                    Key = HostKey,
                    Name = host.GetPlayerName(),
                    HasCharacter = true,
                    CharacterKey = host.GetZDOID().ToString(),
                    Dead = host.IsDead(),
                    Visible = visible,
                    Position = pos,
                    Yaw = host.transform.rotation.eulerAngles.y,
                    Biome = wg != null ? BiomeId.Name(wg.GetBiome(pos.x, pos.z)) : "",
                });
            }

            if (KeyByPeer.Count > 256) KeyByPeer.Clear();
        }

        private static string KeyFor(ZNetPeer peer)
        {
            string key;
            if (KeyByPeer.TryGetValue(peer.m_uid, out key)) return key;
            try
            {
                // Steam id or PlayFab id, depending on the platform the peer connected through.
                key = peer.m_socket != null ? peer.m_socket.GetHostName() : null;
            }
            catch (Exception)
            {
                key = null;
            }
            if (string.IsNullOrEmpty(key) || key == "0") key = "name:" + peer.m_playerName;
            KeyByPeer[peer.m_uid] = key;
            return key;
        }
    }
}
