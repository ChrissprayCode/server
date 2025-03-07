﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Net.Http;
using Common.Networking.Packet;
using Common.Networking.IO;
using ENet;
using GameServer.Server.Packets;
using GameServer.Logging;
using GameServer.Utilities;
using Common.Game;

namespace GameServer.Server
{
    public class ENetServer
    {
        public static ConcurrentBag<Event> Incoming { get; private set; }
        public static ConcurrentQueue<ENetCmds> ENetCmds { get; private set; }
        public static Dictionary<uint, Player> Players { get; private set; }
        public static Dictionary<uint, Peer> Peers { get; private set; }
        public static Dictionary<ServerOpcode, ENetCmd> ENetCmd { get; private set; }
        public static Dictionary<ClientPacketOpcode, HandlePacket> HandlePacket { get; private set; }
        public static HttpClient WebClient { get; private set; }
        public static ServerVersion ServerVersion { get; private set; }
        public static Dictionary<ResourceType, ResourceInfo> ResourceInfoData { get; private set; }
        public static Dictionary<StructureType, StructureInfo> StructureInfoData { get; private set; }
        public static Dictionary<uint, Channel> Channels { get; set; }
        public static uint ChannelId { get; set; }

        #region WorkerThread
        public static void WorkerThread() 
        {
            Thread.CurrentThread.Name = "SERVER";

            ChannelId = 0;
            Channels = new Dictionary<uint, Channel>
            {
                { ChannelId++, new Channel { ChannelName = "Global" } },
                { ChannelId++, new Channel { ChannelName = "Game"   } }
            };

            ResourceInfoData = typeof(ResourceInfo).Assembly.GetTypes().Where(x => typeof(ResourceInfo).IsAssignableFrom(x) && !x.IsAbstract).Select(Activator.CreateInstance).Cast<ResourceInfo>()
                .ToDictionary(x => (ResourceType)Enum.Parse(typeof(ResourceType), x.GetType().Name.Replace(typeof(ResourceInfo).Name, "")), x => x);
            StructureInfoData = typeof(StructureInfo).Assembly.GetTypes().Where(x => typeof(StructureInfo).IsAssignableFrom(x) && !x.IsAbstract).Select(Activator.CreateInstance).Cast<StructureInfo>()
                .ToDictionary(x => (StructureType)Enum.Parse(typeof(StructureType), x.GetType().Name.Replace(typeof(StructureInfo).Name, "")), x => x);

            FileManager.SetupDirectories();
            FileManager.CreateConfig("banned_players", FileManager.ConfigType.Array);

            ValidatePlayerConfigs();

            ServerVersion = new()
            {
                Major = 0,
                Minor = 1,
                Patch = 0
            };

            Incoming = new();
            ENetCmds = new();
            Players = new();
            Peers = new();
            WebClient = new();

            HandlePacket = typeof(HandlePacket).Assembly.GetTypes().Where(x => typeof(HandlePacket).IsAssignableFrom(x) && !x.IsAbstract).Select(Activator.CreateInstance).Cast<HandlePacket>()
                .ToDictionary(x => x.Opcode, x => x);

            ENetCmd = typeof(ENetCmd).Assembly.GetTypes().Where(x => typeof(ENetCmd).IsAssignableFrom(x) && !x.IsAbstract).Select(Activator.CreateInstance).Cast<ENetCmd>()
                .ToDictionary(x => x.Opcode, x => x);

            Library.Initialize();

            var maxClients = 100;
            ushort port = 25565;

            using (var server = new Host())
            {
                var address = new Address
                {
                    Port = port
                };

                server.Create(address, maxClients);

                Logger.Log($"Listening on port {port}");

                while (!Console.KeyAvailable)
                {
                    var polled = false;

                    // Server Instructions
                    while (ENetCmds.TryDequeue(out ENetCmds result))
                    {
                        foreach (var cmd in result.Instructions)
                        {
                            var opcode = cmd.Key;

                            ENetCmd[opcode].Handle(cmd.Value);
                        }
                    }

                    // Incoming
                    while (Incoming.TryTake(out Event netEvent))
                    {
                        var peer = netEvent.Peer;
                        var packetSizeMax = 2048;
                        var readBuffer = new byte[packetSizeMax];
                        var packetReader = new PacketReader(readBuffer);
                        packetReader.BaseStream.Position = 0;

                        if (netEvent.Packet.Length > packetSizeMax) 
                        {
                            Logger.LogWarning($"Tried to read a packet from peer {netEvent.Peer.ID} of size {netEvent.Packet.Length} when the max packet size is {packetSizeMax}");
                            packetReader.Dispose();
                            netEvent.Packet.Dispose();
                            continue;
                        }

                        netEvent.Packet.CopyTo(readBuffer);

                        var opcode = (ClientPacketOpcode)packetReader.ReadByte();

                        Logger.Log($"Received New Client Packet: {opcode}");

                        HandlePacket[opcode].Handle(netEvent, ref packetReader);

                        packetReader.Dispose();
                        netEvent.Packet.Dispose();
                    }

                    while (!polled)
                    {
                        if (server.CheckEvents(out Event netEvent) <= 0)
                        {
                            if (server.Service(15, out netEvent) <= 0)
                                break;

                            polled = true;
                        }

                        var eventType = netEvent.Type;

                        if (eventType == EventType.None) 
                        {
                            Logger.LogWarning("Received EventType.None");
                        }

                        var peer = netEvent.Peer;

                        if (eventType == EventType.Connect) 
                        {
                            var bannedPlayers = FileManager.ReadConfig<List<BannedPlayer>>("banned_players");
                            var bannedPlayer = bannedPlayers.Find(x => x.Ip == peer.IP);

                            if (bannedPlayer != null) 
                            {
                                // Player is banned, disconnect them immediately 
                                peer.DisconnectNow((uint)DisconnectOpcode.Banned);
                                Logger.Log($"Player '{bannedPlayer.Name}' tried to join but is banned");
                                break;
                            }

                            // Player is not banned
                            // Set timeout delays for player timeout
                            if (!Peers.ContainsKey(peer.ID))
                                Peers.Add(peer.ID, peer);
                            peer.Timeout(32, 1000, 4000);
                        }

                        if (eventType == EventType.Disconnect) 
                        {
                            if (Peers.ContainsKey(peer.ID))
                                Peers.Remove(peer.ID);
                            if (Players.ContainsKey(peer.ID)) 
                            {
                                Channels[(uint)SpecialChannel.Global].Users.Remove(peer.ID);

                                var player = Players[peer.ID];
                                player.UpdatePlayerConfig();

                                HandleDisconnectAndTimeout(netEvent);

                                Logger.Log($"Player '{player.Username}' disconnected");
                            }
                        }

                        if (eventType == EventType.Timeout) 
                        {
                            if (Peers.ContainsKey(peer.ID))
                                Peers.Remove(peer.ID);
                            if (Players.ContainsKey(peer.ID)) 
                            {
                                Channels[(uint)SpecialChannel.Global].Users.Remove(peer.ID);

                                var player = Players[peer.ID];
                                player.UpdatePlayerConfig();

                                HandleDisconnectAndTimeout(netEvent);

                                Logger.Log($"Player '{player.Username}' timed out");
                            }
                        }

                        if (eventType == EventType.Receive) 
                        {
                            //Logger.Log("Packet received from - ID: " + netEvent.Peer.ID + ", IP: " + netEvent.Peer.IP + ", Channel ID: " + netEvent.ChannelID + ", Data length: " + netEvent.Packet.Length);

                            Incoming.Add(netEvent);
                        }
                    }
                }

                server.Flush();
            }

            Library.Deinitialize();
        }

        private static void HandleDisconnectAndTimeout(Event netEvent) 
        {
            // Remove player from player list
            Players.Remove(netEvent.Peer.ID);

            // Tell all other clients that this player has left
            ENetServer.Send(new ServerPacket((byte)ServerPacketOpcode.PlayerJoinLeave, new WPacketPlayerJoinLeave
            {
                JoinLeaveOpcode = JoinLeaveOpcode.Leave,
                PlayerId = netEvent.Peer.ID
            }), ENetServer.GetOtherPeers(netEvent.Peer));
        }

        // Gets every peer in ENetServer.Players except for the current 'peer'
        public static List<Peer> GetOtherPeers(Peer peer)
        {
            var peers = new List<Peer>();
            foreach (var p in ENetServer.Players)
                if (p.Key != peer.ID)
                    peers.Add(p.Value.Peer);

            return peers;
        }

        // Gets all peers
        public static List<Peer> GetAllPeers() 
        {
            var peers = new List<Peer>();
            foreach (var p in Players)
                peers.Add(p.Value.Peer);

            return peers;
        }

        public static void SendAll(GamePacket gamePacket) 
        {
            foreach (var peer in Peers.Values)
                Send(gamePacket, peer);
        }

        public static void Send(GamePacket gamePacket, List<Peer> peers)
        {
            foreach (var peer in peers)
                Send(gamePacket, peer);
        }

        public static void Send(GamePacket gamePacket, Peer peer)
        {
            // Send data to a specific client (peer)
            var packet = default(Packet);
            packet.Create(gamePacket.Data, gamePacket.PacketFlags);
            byte channelID = 0;
            peer.Send(channelID, ref packet);

            // DEBUG
            //Logger.Log($"Sending New Server Packet {Enum.GetName(typeof(ServerPacketOpcode), gamePacket.Opcode)} to {peer.ID}");
        }

        public static void SaveAllOnlinePlayersToDatabase()
        {
            foreach (var player in Players.Values) 
            {
                player.AddResourcesGeneratedFromStructures();
                player.UpdatePlayerConfig();
            }

            Logger.Log($"Saved {Players.Count} online players to the database");
        }

        private static void ValidatePlayerConfigs()
        {
            var playerConfigs = Player.GetAllPlayerConfigs();

            // Consider the following scenario:
            // 1. A new structure / resource gets added to the game
            // 2. But the current player config did not get these updated changes
            // 3. That's what this code does, it updates the player config to include these new changes
            var resourceCountTypes = Enum.GetValues(typeof(ResourceType));
            var structureCountTypes = Enum.GetValues(typeof(StructureType));

            foreach (var player in playerConfigs)
            {
                if (player.ResourceCounts.Count < resourceCountTypes.Length)
                    foreach (ResourceType type in Enum.GetValues(typeof(ResourceType)))
                        if (!player.ResourceCounts.ContainsKey(type))
                            player.ResourceCounts.Add(type, 0);

                if (player.StructureCounts.Count < structureCountTypes.Length)
                    foreach (StructureType type in Enum.GetValues(typeof(StructureType)))
                        if (!player.StructureCounts.ContainsKey(type))
                            player.StructureCounts.Add(type, 0);
            }

            Logger.Log("Validated all player configs successfully");
        }
        #endregion
    }

    public struct ServerVersion
    {
        public byte Major { get; set; }
        public byte Minor { get; set; }
        public byte Patch { get; set; }
    }

    public class ENetCmds 
    {
        public Dictionary<ServerOpcode, List<object>> Instructions { get; set; }

        public ENetCmds()
        {
            Instructions = new Dictionary<ServerOpcode, List<object>>();
        }

        public ENetCmds(ServerOpcode opcode)
        {
            Instructions = new Dictionary<ServerOpcode, List<object>>
            {
                [opcode] = null
            };
        }

        public void Set(ServerOpcode opcode, params object[] data)
        {
            Instructions[opcode] = new List<object>(data);
        }
    }

    public enum ServerOpcode 
    {
        GetOnlinePlayers,
        GetPlayerStats,
        KickPlayer,
        BanPlayer,
        PardonPlayer,
        ClearPlayerStats,
        SendPlayerData
    }
}
