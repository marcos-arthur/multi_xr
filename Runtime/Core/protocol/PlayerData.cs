using System;

namespace com.imd.ackit.multi_xr.Protocol
{
    /// <summary>
    /// Informações de player
    /// </summary>
    [Serializable]
    public sealed class PlayerData
    {
        public string playerId;
        public string role;
        public string displayName;
        public string displayColor;
    }
}