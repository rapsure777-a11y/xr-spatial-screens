using System;

namespace XrssCapture
{
    /// <summary>
    /// Shared-memory frame protocol between XrssCapture.exe (writer) and Unity (reader). Kept identical to Assets/Scripts/Capture/FrameProtocol.cs.
    /// Layout: 256-byte header, then two frame slots (BGRA, top row first, tightly packed at <c>stride</c> bytes per row).
    /// The writer fills the slot that is NOT the latest, then publishes (latestSlot, width, height, ..., frameCounter last). A reader copies the latest slot and
    /// accepts the copy only if frameCounter advanced by at most 1 meanwhile.
    /// </summary>
    public static class FrameProtocol
    {
        public const uint Magic = 0x43535258;           // 'XRSC' little-endian
        public const uint Version = 1;
        public const int HeaderSize = 256;
        // Header field offsets
        public const int OffMagic = 0, OffVersion = 4, OffHeaderSize = 8, OffSlotCapacity = 12, OffMaxWidth = 16, OffMaxHeight = 20;
        public const int OffFrameCounter = 24, OffLatestSlot = 28, OffWidth = 32, OffHeight = 36, OffStride = 40, OffFlags = 44;
        public const int OffClientX = 48, OffClientY = 52, OffClientW = 56, OffClientH = 60;
        public const int OffTimestampUs = 64, OffHelperPid = 72, OffHwnd = 76, OffDropped = 80, OffSourceW = 84, OffSourceH = 88, OffCaptureFps = 92;
        // Flags
        public const uint FlagAlive = 1, FlagForeground = 2, FlagMinimized = 4, FlagReady = 8;

        public static long TotalSize(int maxWidth, int maxHeight) => HeaderSize + 2L * SlotCapacity(maxWidth, maxHeight);
        public static int SlotCapacity(int maxWidth, int maxHeight) => maxWidth * maxHeight * 4;
        public static string MapName(string id) => "XrssFrame-" + id;
    }
}
