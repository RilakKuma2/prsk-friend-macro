// SekaiMacro.app for macOS: a native window for the control panel, as the Windows app hosts it in WebView2.
// It starts the panel (Resources/app/SekaiMacro gui --window) in ~/Library/Application Support/SekaiMacro, where
// settings and records stay across app updates, and shows the web UI in a WKWebView. Quitting stops the panel;
// macro runs it started keep going and are stopped from their cards. build-release-mac.sh compiles this file.
import AppKit
import WebKit

let port = 8786
let panelURL = URL(string: "http://127.0.0.1:\(port)/")!
let workspace = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/SekaiMacro")
let bundled = Bundle.main.resourceURL!.appendingPathComponent("app")

// Flow data and templates follow the app; local/ (settings and records) is left alone. Only changed files are
// replaced, each by an atomic rename, since a run that outlived the last panel may be reading them.
func syncData() throws {
    let fm = FileManager.default
    func mirror(_ source: URL, _ target: URL) throws {
        if let current = try? Data(contentsOf: target), current == (try Data(contentsOf: source)) { return }
        let temp = target.deletingLastPathComponent().appendingPathComponent(".\(target.lastPathComponent).new")
        try? fm.removeItem(at: temp)
        try fm.copyItem(at: source, to: temp)
        guard rename(temp.path, target.path) == 0 else { throw CocoaError(.fileWriteUnknown) }
    }
    let source = bundled.appendingPathComponent("templates"), target = workspace.appendingPathComponent("templates")
    try fm.createDirectory(at: target, withIntermediateDirectories: true)
    for name in ["flow.json", "handlers.json", "plan.json", "stages.json"] {
        try mirror(bundled.appendingPathComponent(name), workspace.appendingPathComponent(name))
    }
    let names = try fm.contentsOfDirectory(atPath: source.path)
    for name in names { try mirror(source.appendingPathComponent(name), target.appendingPathComponent(name)) }
    for stale in try fm.contentsOfDirectory(atPath: target.path) where !names.contains(stale) {
        try? fm.removeItem(at: target.appendingPathComponent(stale))
    }
}

// A panel that is already running (an earlier window, or one started from a terminal) is reused.
func panelRunning() -> Bool {
    let fd = socket(AF_INET, SOCK_STREAM, 0)
    guard fd >= 0 else { return false }
    defer { close(fd) }
    var address = sockaddr_in()
    address.sin_family = sa_family_t(AF_INET)
    address.sin_port = in_port_t(port).bigEndian
    address.sin_addr.s_addr = inet_addr("127.0.0.1")
    return withUnsafePointer(to: &address) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0 }
    }
}

func page(_ text: String) -> String {
    let escaped = text.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
    return """
    <meta charset="utf-8"><style>
    body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;font:15px -apple-system,sans-serif;
      background:#f2f4f6;color:#4e5968;padding:0 32px;text-align:center;line-height:1.6}
    @media (prefers-color-scheme:dark){body{background:#111113;color:#a5adb7}}
    </style><body>\(escaped)</body>
    """
}

final class AppDelegate: NSObject, NSApplicationDelegate, WKNavigationDelegate, WKUIDelegate, WKDownloadDelegate {
    var window: NSWindow!
    let webView = WKWebView(frame: .zero, configuration: WKWebViewConfiguration())
    var panel: Process?
    var quitting = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.mainMenu = makeMenu()
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1320, height: 860),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "셐 매크로"
        window.minSize = NSSize(width: 420, height: 560)
        window.contentView = webView
        window.center()
        window.setFrameAutosaveName("Panel")
        webView.navigationDelegate = self
        webView.uiDelegate = self
        webView.loadHTMLString(page("셐 매크로를 여는 중…"), baseURL: nil)
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        if !panelRunning() {
            do { try startPanel() } catch { show("제어판을 시작하지 못했어요.\n\(error.localizedDescription)"); return }
        }
        waitForPanel(until: Date().addingTimeInterval(60))
    }

    func startPanel() throws {
        try syncData()
        let fm = FileManager.default
        let local = workspace.appendingPathComponent("local")
        try fm.createDirectory(at: local, withIntermediateDirectories: true)
        // The panel's output, for when it fails to start; started over once it grows past 5 MB.
        let logURL = local.appendingPathComponent("panel.log")
        let size = (try? fm.attributesOfItem(atPath: logURL.path))?[.size] as? Int ?? 0
        if size > 5 << 20 { try? fm.removeItem(at: logURL) }
        if !fm.fileExists(atPath: logURL.path) { fm.createFile(atPath: logURL.path, contents: nil) }
        let log = try FileHandle(forWritingTo: logURL)
        log.seekToEndOfFile()
        let process = Process()
        process.executableURL = bundled.appendingPathComponent("SekaiMacro")
        process.arguments = ["gui", "--window"]
        process.currentDirectoryURL = workspace
        process.standardOutput = log
        process.standardError = log
        process.terminationHandler = { [weak self] ended in
            DispatchQueue.main.async {
                guard let self, !self.quitting else { return }
                if ended.terminationStatus == 0 { self.quitting = true; NSApp.terminate(nil); return }
                self.show("제어판이 꺼졌어요 (코드 \(ended.terminationStatus)).\n앱을 다시 열어 주세요. 기록: \(logURL.path)")
            }
        }
        try process.run()
        panel = process
    }

    func waitForPanel(until deadline: Date) {
        var request = URLRequest(url: panelURL.appendingPathComponent("api/meta"))
        request.timeoutInterval = 1
        URLSession.shared.dataTask(with: request) { [weak self] _, response, _ in
            DispatchQueue.main.async {
                guard let self else { return }
                if (response as? HTTPURLResponse)?.statusCode == 200 { self.webView.load(URLRequest(url: panelURL)); return }
                if self.panel?.isRunning == false { return }
                if Date() < deadline { DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { self.waitForPanel(until: deadline) } }
                else { self.show("제어판이 응답하지 않아요. 앱을 다시 열어 주세요.") }
            }
        }.resume()
    }

    func show(_ text: String) { webView.loadHTMLString(page(text), baseURL: nil) }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) {
        quitting = true
        guard let panel, panel.isRunning else { return }
        panel.terminate()
        let deadline = Date().addingTimeInterval(5)
        while panel.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.1) }
    }

    // Pages other than the panel (game and emulator sites) open in the default browser.
    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        if action.shouldPerformDownload { decisionHandler(.download); return }
        if let url = action.request.url, ["http", "https"].contains(url.scheme ?? ""), url.host != "127.0.0.1" {
            NSWorkspace.shared.open(url)
            decisionHandler(.cancel)
            return
        }
        decisionHandler(.allow)
    }
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration, for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = action.request.url { NSWorkspace.shared.open(url) }
        return nil
    }
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) { webView.reload() }

    // Saved screenshots go to Downloads.
    func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) { download.delegate = self }
    func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) { download.delegate = self }
    func download(_ download: WKDownload, decideDestinationUsing response: URLResponse, suggestedFilename: String, completionHandler: @escaping (URL?) -> Void) {
        let folder = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
        let name = suggestedFilename as NSString
        var target = folder.appendingPathComponent(suggestedFilename)
        var n = 2
        while FileManager.default.fileExists(atPath: target.path) {
            target = folder.appendingPathComponent("\(name.deletingPathExtension) (\(n))").appendingPathExtension(name.pathExtension)
            n += 1
        }
        completionHandler(target)
    }

    func makeMenu() -> NSMenu {
        let main = NSMenu()
        func submenu(_ title: String, _ items: [NSMenuItem]) {
            let menu = NSMenu(title: title)
            items.forEach(menu.addItem)
            let item = NSMenuItem()
            item.submenu = menu
            main.addItem(item)
        }
        func item(_ title: String, _ action: Selector, _ key: String, target: AnyObject? = nil) -> NSMenuItem {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
            item.target = target
            return item
        }
        submenu("셐 매크로", [
            item("셐 매크로에 관하여", #selector(NSApplication.orderFrontStandardAboutPanel(_:)), ""),
            .separator(),
            item("셐 매크로 가리기", #selector(NSApplication.hide(_:)), "h"),
            item("셐 매크로 종료", #selector(NSApplication.terminate(_:)), "q"),
        ])
        // Without an Edit menu, Command-C and Command-V do not reach the page's text fields.
        submenu("편집", [
            item("실행 취소", Selector(("undo:")), "z"),
            item("실행 복귀", Selector(("redo:")), "Z"),
            .separator(),
            item("오려두기", #selector(NSText.cut(_:)), "x"),
            item("복사하기", #selector(NSText.copy(_:)), "c"),
            item("붙여넣기", #selector(NSText.paste(_:)), "v"),
            item("전체 선택", #selector(NSText.selectAll(_:)), "a"),
        ])
        submenu("보기", [item("새로 고침", #selector(WKWebView.reload(_:)), "r", target: webView)])
        submenu("윈도우", [
            item("최소화", #selector(NSWindow.performMiniaturize(_:)), "m"),
            item("닫기", #selector(NSWindow.performClose(_:)), "w"),
        ])
        return main
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
