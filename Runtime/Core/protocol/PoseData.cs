using System;
using UnityEngine;

namespace com.imd.ackit.multi_xr.Protocol
{
    /// <summary>
    /// Posição e rotação de uma parte do avatar,
    /// relativas ao NetworkOrigin da sessão.
    /// </summary>
    [Serializable]
    public sealed class PoseData
    {
        public PositionData position;
        public RotationData rotation;
    }

    [Serializable]
    public class PositionData
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public class RotationData
    {
        public float x;
        public float y;
        public float z;
        public float w;
    }
}