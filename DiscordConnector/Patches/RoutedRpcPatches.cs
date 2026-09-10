using System;
using System.Collections.Generic;

using HarmonyLib;

using UnityEngine;

namespace DiscordConnector.Patches;

internal static class RoutedRpcPatches
{
    private const double DuplicateWindowSeconds = 2d;
    private static readonly int s_chatMessageHash = "ChatMessage".GetStableHashCode();
    private static readonly Dictionary<long, RecentShout> s_recentShouts = new();

    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    private static class RPC_RoutedRPC
    {
        private static void Prefix(ZRpc rpc, ZPackage pkg)
        {
            CapturePlayerShout(rpc, pkg);
        }
    }

    internal static void ForwardPlayerShout(ZNetPeer peer, Vector3 position, string text, string source)
    {
        if (IsDuplicate(peer.m_uid, position))
        {
            DiscordConnectorPlugin.StaticLogger.LogDebug(
                $"Ignored duplicate player shout from {peer.m_playerName} received through {source}");
            return;
        }

        DiscordConnectorPlugin.StaticLogger.LogInfo(
            $"Received player shout from {peer.m_playerName} through {source}");
        Handlers.Shout(peer, position, text);
    }

    private static void CapturePlayerShout(ZRpc sourceRpc, ZPackage routedPackage)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            ZPackage routedPackageCopy = new(routedPackage.GetArray());
            ZRoutedRpc.RoutedRPCData routedRpcData = new();
            routedRpcData.Deserialize(routedPackageCopy);

            if (routedRpcData.m_methodHash != s_chatMessageHash)
            {
                return;
            }

            ZNetPeer peer = ZNet.instance.GetPeer(routedRpcData.m_senderPeerID);
            if (peer == null || peer.m_rpc != sourceRpc)
            {
                DiscordConnectorPlugin.StaticLogger.LogWarning(
                    "Ignored routed player shout because the sender did not match its connection");
                return;
            }

            ZPackage parameters = new(routedRpcData.m_parameters.GetArray());
            Vector3 position = parameters.ReadVector3();
            Talker.Type talkerType = (Talker.Type)parameters.ReadInt();
            UserInfo userInfo = new();
            userInfo.Deserialize(ref parameters);
            string text = parameters.ReadString();

            if (talkerType != Talker.Type.Shout ||
                string.IsNullOrWhiteSpace(text) ||
                text == RPC.ChatMessageDetail.EmptyTextMessage ||
                text == ChatPatches.ArrivalShout)
            {
                return;
            }

            ForwardPlayerShout(peer, position, text, "native routed chat");
        }
        catch (Exception exception)
        {
            DiscordConnectorPlugin.StaticLogger.LogDebug(
                $"Could not inspect routed chat message: {exception.Message}");
        }
    }

    private static bool IsDuplicate(long senderPeerId, Vector3 position)
    {
        DateTime now = DateTime.UtcNow;

        lock (s_recentShouts)
        {
            if (s_recentShouts.TryGetValue(senderPeerId, out RecentShout recentShout) &&
                (now - recentShout.ReceivedAt).TotalSeconds <= DuplicateWindowSeconds &&
                Vector3.SqrMagnitude(position - recentShout.Position) < 0.01f)
            {
                return true;
            }

            s_recentShouts[senderPeerId] = new RecentShout(now, position);
            return false;
        }
    }

    private readonly struct RecentShout(DateTime receivedAt, Vector3 position)
    {
        internal DateTime ReceivedAt { get; } = receivedAt;
        internal Vector3 Position { get; } = position;
    }
}
