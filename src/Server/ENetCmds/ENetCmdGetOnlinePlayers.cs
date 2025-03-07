﻿using System.Collections.Generic;
using GameServer.Logging;

namespace GameServer.Server
{
    public class ENetCmdGetOnlinePlayers : ENetCmd
    {
        public override ServerOpcode Opcode { get; set; }

        public ENetCmdGetOnlinePlayers()
        {
            Opcode = ServerOpcode.GetOnlinePlayers;
        }

        public override void Handle(List<object> value)
        {
            if (ENetServer.Players.Count == 0)
            {
                Logger.Log("There are 0 players on the server");
                return;
            }

            var players = new List<string>();
            foreach (var p in ENetServer.Players)
                players.Add($"{p.Value} ({p.Key})");

            Logger.LogRaw($"\nOnline Players: {string.Join(' ', players)}");
        }
    }
}
