using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimWebMap
{
    internal struct PlayerEntry
    {
        public long Id;
        public string Name;
        /// <summary>The player's in-game "visible to other players" map setting.</summary>
        public bool Visible;
        public Vector3 Position;
        public float Yaw;
        public string Biome;
    }

    /// <summary>Reads connected players from ZNet. Main thread only.</summary>
    internal static class PlayerTracker
    {
        // Only the server's own (non-dedicated host) player keeps this flag on ZNet rather than on a peer.
        private static readonly FieldInfo HostPublicRefPos =
            typeof(ZNet).GetField("m_publicReferencePosition", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Collect(ZNet znet, List<PlayerEntry> into)
        {
            into.Clear();
            ZDOMan zdoMan = ZDOMan.instance;
            WorldGenerator wg = WorldGenerator.instance;

            foreach (ZNetPeer peer in znet.GetPeers())
            {
                if (peer == null || !peer.IsReady() || peer.m_server) continue;
                if (peer.m_characterID.IsNone() || string.IsNullOrEmpty(peer.m_playerName)) continue;

                ZDO zdo = zdoMan != null ? zdoMan.GetZDO(peer.m_characterID) : null;
                var entry = new PlayerEntry
                {
                    Id = peer.m_uid,
                    Name = peer.m_playerName,
                    Visible = peer.m_publicRefPos,
                    Position = zdo != null ? zdo.GetPosition() : peer.m_refPos,
                    Yaw = zdo != null ? zdo.GetRotation().eulerAngles.y : 0f,
                };
                entry.Biome = wg != null ? BiomeId.Name(wg.GetBiome(entry.Position.x, entry.Position.z)) : "";
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
                    Name = host.GetPlayerName(),
                    Visible = visible,
                    Position = pos,
                    Yaw = host.transform.rotation.eulerAngles.y,
                    Biome = wg != null ? BiomeId.Name(wg.GetBiome(pos.x, pos.z)) : "",
                });
            }
        }
    }
}
