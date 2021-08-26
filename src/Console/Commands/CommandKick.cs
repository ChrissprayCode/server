﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GameServer.Database;
using GameServer.Server;

namespace GameServer.Logging.Commands
{
    public class CommandKick : Command
    {
        public override string Description { get; set; }
        public override string Usage { get; set; }
        public override string[] Aliases { get; set; }

        public CommandKick()
        {
            Description = "Kick a player";
            Usage = "<player>";
        }

        public override void Run(string[] args) 
        {
            if (args.Length == 0) 
            {
                Logger.Log("Please provide a player name to kick");
                return;
            }

            var cmd = new ServerInstructions();
            cmd.Set(ServerInstructionOpcode.KickPlayer, args[0]);

            ENetServer.ServerInstructions.Add(cmd);
        }
    }
}
