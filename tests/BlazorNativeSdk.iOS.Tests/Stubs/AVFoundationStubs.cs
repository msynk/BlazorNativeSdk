// Stand-in for the AVFoundation binding enum used by AppleBarcodeMapping (linked source). On Apple platforms its members are
// backed by string constants (AVMetadataObjectTypeQRCode, ...), so the mapping depends on member names only; the names and flag
// values mirror Microsoft.iOS.dll (net10.0_26.5). Only the barcode members (and Face, a non-barcode type) are needed.
namespace AVFoundation;

[Flags]
public enum AVMetadataObjectType : ulong
{
    None = 0,
    Face = 1,
    AztecCode = 2,
    Code128Code = 4,
    Code39Code = 8,
    Code39Mod43Code = 16,
    Code93Code = 32,
    EAN13Code = 64,
    EAN8Code = 128,
    PDF417Code = 256,
    QRCode = 512,
    UPCECode = 1024,
    Interleaved2of5Code = 2048,
    ITF14Code = 4096,
    DataMatrixCode = 8192,
    CodabarCode = 262144,
    GS1DataBarCode = 524288,
    GS1DataBarExpandedCode = 1048576,
    GS1DataBarLimitedCode = 2097152,
    MicroQRCode = 4194304,
    MicroPdf417Code = 8388608,
}
