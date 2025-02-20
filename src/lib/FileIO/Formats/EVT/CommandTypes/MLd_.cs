using System;
using System.Collections.Generic;
using System.Diagnostics;

using Serialization;

namespace EVTUI;

public partial class CommandTypes
{
    public class MLd_ : ISerializable
    {
        public const int DataSize = 16;

        public UInt32[] UNUSED_UINT32 = new UInt32[8];

        public void ExbipHook<T>(T rw, Dictionary<string, object> args) where T : struct, IBaseBinaryTarget
        {
            rw.RwUInt32s(ref this.UNUSED_UINT32, 4);
            for (int i=0; i<this.UNUSED_UINT32.Length; i++)
                Trace.Assert(this.UNUSED_UINT32[i] == 0, $"Unexpected nonzero value ({this.UNUSED_UINT32[i]}) in reserve variable.");
        }
    }
}
