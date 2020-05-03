namespace Ryujinx.Common.Configuration.Hid
{
    public enum PlayerIndex : int // This enum was duplicated from Ryujinx.HLE.HOS.Services.Hid.PlayerIndex and should be kept identical
    {
        Player1  = 0,
        Player2  = 1,
        Player3  = 2,
        Player4  = 3,
        Player5  = 4,
        Player6  = 5,
        Player7  = 6,
        Player8  = 7,
        Handheld = 8,
        Unknown  = 9,
        Auto     = 10    // Shouldn't be used directly
    }
}