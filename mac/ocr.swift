// Japanese OCR for the macOS build: prints the text lines of an image, top to bottom and left to right,
// like the Windows OCR the macro uses there. The app compiles this with swiftc on first use.
// usage: sekai-ocr <image> [scale]   (scale enlarges the image first, as small text reads better that way)
import CoreGraphics
import Foundation
import ImageIO
import Vision

let arguments = CommandLine.arguments
guard arguments.count == 2 || arguments.count == 3, let scale = Int(arguments.count == 3 ? arguments[2] : "1"), (1...8).contains(scale) else {
    FileHandle.standardError.write("usage: sekai-ocr <image> [scale 1-8]\n".data(using: .utf8)!)
    exit(2)
}
let url = URL(fileURLWithPath: arguments[1])
guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
      let original = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
    FileHandle.standardError.write("cannot read \(url.path)\n".data(using: .utf8)!)
    exit(1)
}
var image = original
if scale > 1 {
    let width = original.width * scale, height = original.height * scale
    guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { exit(1) }
    context.interpolationQuality = .high
    context.draw(original, in: CGRect(x: 0, y: 0, width: width, height: height))
    guard let enlarged = context.makeImage() else { exit(1) }
    image = enlarged
}

let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate
request.recognitionLanguages = ["ja-JP", "en-US"]
// Names and episode numbers are read as written, not corrected toward dictionary words.
request.usesLanguageCorrection = false
do {
    try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
} catch {
    FileHandle.standardError.write("\(error)\n".data(using: .utf8)!)
    exit(1)
}

// Vision's boxes are normalized with the origin at the bottom left. Boxes whose centers are within half a
// box height of a row's first box share that row and are read left to right.
let words = (request.results ?? []).compactMap { observation -> (box: CGRect, text: String)? in
    guard let best = observation.topCandidates(1).first else { return nil }
    return (observation.boundingBox, best.string)
}.sorted { $0.box.midY > $1.box.midY }
var rows: [[(box: CGRect, text: String)]] = []
for word in words {
    if let first = rows.last?.first, abs(first.box.midY - word.box.midY) < first.box.height / 2 {
        rows[rows.count - 1].append(word)
    } else {
        rows.append([word])
    }
}
for row in rows {
    print(row.sorted { $0.box.minX < $1.box.minX }.map { $0.text }.joined(separator: " "))
}
