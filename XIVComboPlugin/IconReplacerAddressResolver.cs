using Dalamud.Game;
using Dalamud.Game.Internal;
using System;

namespace XIVComboPlugin
{
    class IconReplacerAddressResolver : BaseAddressResolver
    {
        public IntPtr GetIcon { get; private set; }
        public IntPtr IsIconReplaceable { get; private set; }

        public IntPtr RequestAction { get; private set; }

        protected override void Setup64Bit(SigScanner sig)
        {
            this.GetIcon = sig.ScanText("48 89 5c 24 08 48 89 6c 24 10 48 89 74 24 18 57 48 83 ec 30 8b da be dd 1c 00 00 bd d3 0d 00 00");
            //this.GetIcon = sig.ScanText("E8 ?? ?? ?? ?? F6 DB 8B C8");

            this.IsIconReplaceable = sig.ScanText("81 f9 2e 01 00 00 7f 39 81 f9 2d 01 00 00 0f 8d 11 02 00 00 83 c1 eb");
            //this.IsIconReplaceable = sig.ScanText("81 F9 ?? ?? ?? ?? 7F 39 81 F9 ?? ?? ?? ??");

            this.RequestAction = sig.ScanText("40 53 55 57 41 54 41 57 48 83 EC 60 83 BC 24 ?? ?? ?? ?? ?? 49 8B E9 45 8B E0 44 8B FA 48 8B F9 41 8B D8 74 14 80 79 68 00 74 0E 32 C0 48 83 C4 60 41 5F 41 5C 5F 5D 5B C3");
        }
    }
}
