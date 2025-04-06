using System.Collections.Generic;

namespace Warden
{
    public class PlayerInfo
    {
        public static List<string> PlayersOnline = new List<string>();
        public static List<string> PlayerBans = new List<string>();
        public static int ID { get; set; }
        public static string UserName { get; set; }
        public static string UserIP { get; set; }
        public static string UUID { get; set; }
        public static int ItemStack { get; set; }
        public static TShockAPI.TSPlayer TSPlayer { get; set; }
        public static string CacheIP;
    }
}
