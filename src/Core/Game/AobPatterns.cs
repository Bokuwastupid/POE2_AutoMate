namespace POE2_AutoMate.Core.Game;

public sealed record AobPattern(byte?[] Bytes, int DispOffset, int InstrLen, string Description);

public static class AobPatterns
{
    public static readonly AobPattern[] InGameStateRefs =
    [
        new(
            Bytes:
            [
                0x8B, 0xC7, 0x48, 0x83, 0xC4, 0x40, 0x5F, 0xC3,
                0x48, 0x8B, 0x05, null, null, null, null,
                0x48, 0x89, 0x07, 0x48
            ],
            DispOffset: 11,
            InstrLen: 15,
            Description: "InGameState global slot")
    ];

    public static readonly AobPattern[] GameStateRefs =
    [
        new(
            Bytes:
            [
                0x48, 0x39, 0x2D, null, null, null, null,
                0x0F, 0x85, 0x16, 0x01, 0x00, 0x00
            ],
            DispOffset: 3,
            InstrLen: 7,
            Description: "GameState global slot")
    ];
}
