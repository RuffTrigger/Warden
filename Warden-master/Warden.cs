using System;
using System.Collections.Generic;
using System.IO;
using System.Timers;
using Mono.Data.Sqlite;
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
            3988, // Alpha Bug Net (unobtainable through normal gameplay)
            // Add more unobtainable/debug items here as needed
        };

        private const int MaxStackLimit = 9999;

        private string dbPath = Path.Combine(TShock.SavePath, "ItemTracker.sqlite");
        private SqliteConnection db;

        public Warden(Main game) : base(game)
        {
            Order = 1;
        }

        public override void Initialize()
        {
            playerScanTimer = new Timer(scanInterval);
            playerScanTimer.Elapsed += PlayerScan;
            playerScanTimer.Start();

            ServerApi.Hooks.GameInitialize.Register(this, OnGameInitialize);
            ServerApi.Hooks.NetGetData.Register(this, OnGetData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                playerScanTimer.Stop();
                playerScanTimer.Dispose();
                db?.Close();

                ServerApi.Hooks.GameInitialize.Deregister(this, OnGameInitialize);
                ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            }
            base.Dispose(disposing);
        }

        private void OnGameInitialize(EventArgs args)
        {
            db = new SqliteConnection("Data Source=" + dbPath);
            db.Open();
            using (var cmd = new SqliteCommand("CREATE TABLE IF NOT EXISTS TrackedItems (Id INTEGER PRIMARY KEY AUTOINCREMENT, UniqueIdentifier TEXT NOT NULL, ItemID INTEGER NOT NULL, Stack INTEGER, Source TEXT, CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP);", db))
            {
                cmd.ExecuteNonQuery();
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
                    TShock.Log.ConsoleError("[Warden] Error parsing item drop: " + ex.Message);
                }
            }
        }

        private void StoreItem(string uid, int itemId, int stack, string source)
        {
            using (var cmd = new SqliteCommand("INSERT INTO TrackedItems (UniqueIdentifier, ItemID, Stack, Source) VALUES (@uid, @itemId, @stack, @source);", db))
            {
                cmd.Parameters.AddWithValue("@uid", uid);
                cmd.Parameters.AddWithValue("@itemId", itemId);
                cmd.Parameters.AddWithValue("@stack", stack);
                cmd.Parameters.AddWithValue("@source", source);
                cmd.ExecuteNonQuery();
            }
        }

        private static bool CheckBans(string username)
        {
            using (var reader = TShock.DB.QueryReader("SELECT * FROM Bans WHERE Name = @0", username))
            {
                return reader.Read();
            }
        }

        public static void BanTheCheaters(string ip, string username, UserAccount userAccount)
        {
            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(username))
                return;

            try
            {
                if (!CheckBans(username))
                {
                    Console.WriteLine("Cheater detected - banning");
                    string banReason = "Item hacks";

                    int playerId = -1;
                    string playerUUID = string.Empty;

                    using (var reader = TShock.DB.QueryReader("SELECT * FROM Users WHERE Username = @0", username))
                    {
                        if (reader.Read())
                        {
                            playerId = reader.Get<int>("ID");
                            playerUUID = reader.Get<string>("UUID");
                        }
                    }

                    TShock.Bans.InsertBan(playerUUID, banReason, "Warden", DateTime.Now, DateTime.MaxValue);
                    TSPlayer.All.SendMessage($"# # # {username} was banned due to item hacks... Goodbye! # # #", 205, 0, 55);
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error(ex.ToString());
                Console.WriteLine(ex.ToString());
            }
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
                if (item.active && IsIllegalItem(item.type))
                {
                    return true;
                }

                if (item.active && item.stack > MaxStackLimit)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsIllegalItem(int itemType)
        {
            return illegalItemTypes.Contains(itemType);
        }

        private void PurgeUntrackedItems(TSPlayer player)
        {
            var tPlayer = player.TPlayer;
            bool inventoryChanged = false;

            for (int i = 0; i < tPlayer.inventory.Length; i++)
            {
                var item = tPlayer.inventory[i];
                if (item != null && item.netID != 0 && item.stack > 0)
                {
                    using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM TrackedItems WHERE ItemID = @id AND Stack = @stack LIMIT 1", db))
                    {
                        cmd.Parameters.AddWithValue("@id", item.netID);
                        cmd.Parameters.AddWithValue("@stack", item.stack);

                        var result = Convert.ToInt32(cmd.ExecuteScalar());
                        if (result == 0)
                        {
                            tPlayer.inventory[i].SetDefaults(0);
                            inventoryChanged = true;
                            player.SendWarningMessage($"Untracked item removed: {item.Name}");
                        }
                    }
                }
            }

            if (inventoryChanged)
            {
                for (int i = 0; i < NetItem.MaxInventory; i++)
                {
                    NetMessage.SendData((int)PacketTypes.PlayerSlot, -1, -1, null, player.Index, i);
                }
            }
        }
    }
}
