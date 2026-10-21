// Stand-in for the Vision binding enum used by AppleBarcodeMapping (linked source). On Apple platforms its members are backed
// by string constants (VNBarcodeSymbologyQR, ...), so the mapping depends on member names only; the names, and for fidelity the
// values, mirror Microsoft.iOS.dll (net10.0_26.5). Keep them in sync when the mapping starts using a new member.
namespace Vision;

public enum VNBarcodeSymbology
{
    Aztec = 0,
    Code39 = 1,
    Code39Checksum = 2,
    Code39FullAscii = 3,
    Code39FullAsciiChecksum = 4,
    Code93 = 5,
    Code93i = 6,
    Code128 = 7,
    DataMatrix = 8,
    Ean8 = 9,
    Ean13 = 10,
    I2OF5 = 11,
    I2OF5Checksum = 12,
    Itf14 = 13,
    Pdf417 = 14,
    QR = 15,
    Upce = 16,
    Codabar = 17,
    GS1DataBar = 18,
    GS1DataBarExpanded = 19,
    GS1DataBarLimited = 20,
    MicroPdf417 = 21,
    MicroQR = 22,
    MsiPlessey = 23,
}
