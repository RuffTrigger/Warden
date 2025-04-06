using System;
using System.Collections.Generic;
using System.IO;
using System.Timers;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace Warden
{
    [ApiVersion(2, 1)]
    public class Warden : TerrariaPlugin
    {
        public override string Name => "Warden";
        public override Version Version => new Version(1, 1);
        public override string Author => "Ruff Trigger";
        public override string Description => "A plugin that detects cheaters, tracks item creation, and removes untracked items.";

        private Timer playerScanTimer;
        private readonly int scanInterval = 1500;
        private static readonly HashSet<int> illegalItemTypes = new HashSet<int>
        {
            3988, // Alpha Bug Net (unobtainable)
        };

        private const int MaxStackLimit = 9999;

        public Warden(Main game) : base(game)
        {
            Order = 1;
        }

        public override void Initialize()
        {
            EnsureDatabaseSchema();

            playerScanTimer = new Timer(scanInterval);
            playerScanTimer.Elapsed += PlayerScan;
            playerScanTimer.Start();

            ServerApi.Hooks.NetGetData.Register(this, OnGetData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                playerScanTimer.Stop();
                playerScanTimer.Dispose();

                ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            }
            base.Dispose(disposing);
        }

        private void EnsureDatabaseSchema()
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS TrackedItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UniqueIdentifier TEXT NOT NULL,
                    ItemID INTEGER NOT NULL,
                    Stack INTEGER,
                    Source TEXT,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                );";

            try
            {
                TShock.DB.Query(sql);
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[Warden] Error ensuring database schema: {ex.Message}");
            }
        }

        private void OnGetData(GetDataEventArgs args)
        {
            if (args.MsgID == PacketTypes.ItemDrop)
            {
                try
                {
                    using (var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length)))
                    {
                        byte itemIndex = reader.ReadByte();
                        short posX = reader.ReadInt16();
                        short posY = reader.ReadInt16();
                        short velocityX = reader.ReadInt16();
                        short velocityY = reader.ReadInt16();
                        short stack = reader.ReadInt16();
                        byte prefix = reader.ReadByte();
                        short itemId = reader.ReadInt16();

                        string uid = Guid.NewGuid().ToString();
                        StoreItem(uid, itemId, stack, "Drop");

                        var player = TShock.Players[args.Msg.whoAmI];
                        player?.SendInfoMessage($"Item tracked with ID: {uid}");
                    }
                }
                catch (Exception ex)
                {
                    TShock.Log.ConsoleError($"[Warden] Error parsing item drop: {ex.Message}");
                }
            }
        }

        private void StoreItem(string uid, int itemId, int stack, string source)
        {
            TShock.DB.Query("INSERT INTO TrackedItems (UniqueIdentifier, ItemID, Stack, Source) VALUES (@0, @1, @2, @3);",
                            uid, itemId, stack, source);
        }

        private void PlayerScan(object sender, ElapsedEventArgs e)
        {
            foreach (TSPlayer player in TShock.Players)
            {
                if (player == null || !player.Active || player.Account == null) continue;

                if (IsCheater(player))
                {
                    BanTheCheaters(player.IP, player.Name, player.Account);
                }

                PurgeUntrackedItems(player);
            }
        }

        private static bool IsCheater(TSPlayer player)
        {
            foreach (var item in player.TPlayer.inventory)
            {
                if (item.active && (illegalItemTypes.Contains(item.type) || item.stack > MaxStackLimit))
                {
                    return true;
                }
            }
            return false;
        }

        public static void BanTheCheaters(string ip, string username, UserAccount userAccount)
        {
            if (string.IsNullOrWhiteSpace(username))
                return;

            try
            {
                var existingBan = TShock.DB.QueryScalar<int>("SELECT COUNT(*) FROM Bans WHERE Name=@0", username);
                if (existingBan == 0)
                {
                    string banReason = "Item hacks";
                    TShock.Bans.InsertBan(userAccount.UUID, banReason, "Warden", DateTime.UtcNow, DateTime.MaxValue);
                    TSPlayer.All.SendMessage($"{username} was banned due to item hacks.", 205, 0, 55);
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error(ex.ToString());
            }
        }

        private void PurgeUntrackedItems(TSPlayer player)
        {
            var inventory = player.TPlayer.inventory;
            for (int i = 0; i < inventory.Length; i++)
            {
                var item = inventory[i];
                if (item == null || item.netID == 0 || item.stack <= 0) continue;

                var result = TShock.DB.QueryScalar<int>("SELECT COUNT(*) FROM TrackedItems WHERE ItemID = @0 AND Stack = @1 LIMIT 1", item.netID, item.stack);
                if (result == 0)
                {
                    inventory[i].SetDefaults(0);
                    player.SendWarningMessage($"Untracked item removed: {item.Name}");
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, i);
                }
            }
        }
    }
}
