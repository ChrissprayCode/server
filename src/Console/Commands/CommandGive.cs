﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GameServer.Server;
using Common.Game;
using Common.Utils;
using Common.Networking.Packet;
using GameServer.Server.Packets;
using ENet;

namespace GameServer.Logging.Commands
{
    public class CommandGive : Command
    {
        public override string Description { get; set; }
        public override string Usage { get; set; }
        public override string[] Aliases { get; set; }

        public CommandGive() 
        {
            Description = "Give resources to a player";
            Usage = "<player> <resource> <amount>";
        }

        public override void Run(string[] args) 
        {
            if (args.Length == 0) 
            {
                Logger.Log($"Usage: give {Usage}");
                return;
            }

            // First see if the player is online
            var isOffline = false;
            Player player = null;
            foreach (var p in ENetServer.Players.Values)
            {
                if (p.Username == args[0])
                    player = p;
            }

            if (player == null) 
            {
                // Player was not found to be online, lets search the offline configs
                var playerConfigs = Player.GetAllPlayerConfigs();
                foreach (var p in playerConfigs)
                {
                    if (p.Username.Equals(args[0]))
                        player = p;
                }

                // Could not find a offline player by this username
                if (player == null) 
                {
                    Logger.Log($"Player by the username '{args[0]}' was not found");
                    return;
                }

                // Found a offline player with this username
                isOffline = true;
            }
            
            if (args.Length < 2) 
            {
                Logger.Log("Please specify a resource type");
                return;
            }

            if (!Enum.TryParse(args[1].ToTitleCase(), out ResourceType resourceType))
            {
                Logger.Log($"'{args[1].ToTitleCase()}' is not a valid resource type");
                return;
            }

            if (args.Length < 3) 
            {
                Logger.Log("Please specify an amount");
                return;
            }

            if (!uint.TryParse(args[2], out uint amount))
            {
                Logger.Log($"'{args[2]}' is not a valid number");
                return;
            }

            player.ResourceCounts[resourceType] = player.ResourceCounts[resourceType] + amount;

            // Although this log message is sent before the logic, it would be awkward to put it after and would require a duplicate so that's why it's here right now
            Logger.Log($"Player '{player.Username}' now has {player.ResourceCounts[resourceType]} {resourceType}");

            // Player is offline there is no need to send packet data to a non existant client
            if (isOffline) 
            {
                // Save the players config
                player.UpdatePlayerConfig();
                return;
            }

            // Send the resource data to the player client
            var resourcesToSend = new Dictionary<ResourceType, uint>
            {
                { resourceType, (uint)player.ResourceCounts[resourceType] }
            };

            ENetServer.Send(new ServerPacket((byte)ServerPacketOpcode.PlayerData, new WPacketPlayerData
            {
                ResourceCounts = resourcesToSend
            }), player.Peer, PacketFlags.Reliable);
        }
    }
}
