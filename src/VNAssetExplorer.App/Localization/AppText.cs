using System.Globalization;

namespace VNAssetExplorer.App.Localization;

public sealed record LanguageOption(string Code, string Name);

public sealed class AppText
{
    public static IReadOnlyList<LanguageOption> Languages { get; } = Array.AsReadOnly(new LanguageOption[]
    {
        new("en", "English"), new("ja", "日本語"), new("zh-Hans", "简体中文"), new("zh-Hant", "繁體中文"),
    });

    private readonly int language;
    private readonly CultureInfo culture;

    public AppText(string? code = null)
    {
        language = code switch { "ja" => 1, "zh-Hans" => 2, "zh-Hant" => 3, _ => 0 };
        culture = CultureInfo.GetCultureInfo(Code);
    }

    public string Code => Languages[language].Code;
    public string this[string key] => Strings[key].InLanguage(language);
    public string Format(string key, params object[] arguments) => string.Format(culture, this[key], arguments);

    private sealed record Translation(string English, string Japanese, string Simplified, string Traditional)
    {
        public string InLanguage(int index) => index switch { 1 => Japanese, 2 => Simplified, 3 => Traditional, _ => English };
    }

    private static readonly Dictionary<string, Translation> Strings = new(StringComparer.Ordinal)
    {
        ["Title"] = new("WITCH ON THE HOLY NIGHT Asset Extractor", "魔法使いの夜 素材抽出ツール", "魔法使之夜资源提取工具", "魔法使之夜資源擷取工具"),
        ["GameTitle"] = new("WITCH ON THE HOLY NIGHT", "魔法使いの夜", "魔法使之夜", "魔法使之夜"),
        ["Subtitle"] = new("Asset Extractor", "素材抽出", "资源提取", "資源擷取"),
        ["Language"] = new("Language", "言語", "语言", "語言"),
        ["Source"] = new("Game folder", "ゲームフォルダー", "游戏目录", "遊戲資料夾"),
        ["Output"] = new("Output folder", "出力フォルダー", "输出目录", "輸出資料夾"),
        ["Browse"] = new("Browse…", "参照…", "选择目录…", "選擇資料夾…"),
        ["PickSource"] = new("Select game folder", "ゲームフォルダーを選択", "选择游戏目录", "選擇遊戲資料夾"),
        ["PickOutput"] = new("Select output folder", "出力フォルダーを選択", "选择输出目录", "選擇輸出資料夾"),
        ["SelectCategories"] = new("What to extract", "抽出する素材", "选择想提取的内容", "選擇要擷取的內容"),
        ["AllAssets"] = new("All assets", "すべての素材", "全部资源", "全部資源"),
        ["AllImages"] = new("All images (PNG)", "すべての画像 (PNG)", "全部图片（PNG）", "全部圖片（PNG）"),
        ["AllAudio"] = new("Audio (Ogg)", "音声 (Ogg)", "音频（Ogg）", "音訊（Ogg）"),
        ["AllVideo"] = new("Video (MP4)", "動画 (MP4)", "视频（MP4）", "影片（MP4）"),
        ["Gallery"] = new("In-game gallery", "ゲーム内ギャラリー", "游戏画廊", "遊戲畫廊"),
        ["GalleryImagesOption"] = new("Gallery images (CGs and backgrounds)", "ギャラリー画像（CG・背景）", "画廊图片（CG 与背景）", "畫廊圖片（CG 與背景）"),
        ["GalleryAudioOption"] = new("Music gallery", "音楽ギャラリー", "音频画廊（音乐）", "音樂畫廊"),
        ["Start"] = new("Start extraction", "抽出開始", "开始提取", "開始擷取"),
        ["Cancel"] = new("Cancel", "キャンセル", "取消", "取消"),
        ["OpenOutput"] = new("Open output folder", "出力フォルダーを開く", "打开输出位置", "開啟輸出資料夾"),
        ["Results"] = new("Results", "結果", "结果", "結果"),
        ["Status"] = new("Extraction status", "抽出状況", "提取状态", "擷取狀態"),
        ["Progress"] = new("Archive entry progress", "アーカイブ内の処理進捗", "归档条目工作进度", "封存檔項目處理進度"),
        ["Ready"] = new("Ready", "開始前", "尚未开始", "尚未開始"),
        ["InvalidDirectory"] = new("Invalid folder. Select a standard absolute path.", "フォルダーが無効です。通常の絶対パスを選択してください。", "目录无效，请选择普通绝对路径。", "資料夾無效，請選擇一般的絕對路徑。"),
        ["Scanning"] = new("Scanning…", "スキャン中…", "正在扫描", "正在掃描"),
        ["Classifying"] = new("Identifying assets…", "素材を識別中…", "正在识别", "正在辨識"),
        ["Writing"] = new("Writing files…", "ファイルを書き込み中…", "正在写入", "正在寫入"),
        ["ScanTotalUnknown"] = new("Scan total is unknown", "スキャン対象の総数は不明です", "扫描总量未知", "掃描總數未知"),
        ["WorkProgress"] = new("Archive: {0}\nCategory: {1} · Archive entries processed: {2} / {3}", "アーカイブ：{0}\n種類：{1}／処理済み項目：{2} / {3}", "归档：{0}\n类别：{1}；归档条目工作：{2} / {3}", "封存檔：{0}\n類別：{1}；已處理項目：{2} / {3}"),
        ["Unknown"] = new("Unknown", "不明", "未知", "未知"),
        ["Unspecified"] = new("Unspecified", "未指定", "尚未确定", "尚未確定"),
        ["Cancelled"] = new("Cancelled", "キャンセルしました", "已取消", "已取消"),
        ["Cancelling"] = new("Cancelling. Waiting for extraction to stop…", "キャンセル中。抽出の停止を待っています…", "正在取消，等待提取停止", "正在取消，等待擷取停止"),
        ["Stopped"] = new("Stopped due to an error", "エラーのため停止しました", "已停止，存在错误", "已停止，發生錯誤"),
        ["CompletedWithIssues"] = new("Finished with failures or skipped scan paths", "処理が終了しました。一部の処理に失敗したか、スキャンできない場所がありました", "处理结束，存在失败或扫描遗漏", "處理結束，部分項目失敗或未掃描"),
        ["Completed"] = new("Finished", "処理が終了しました", "处理结束", "處理結束"),
        ["NoStatistics"] = new("No statistics were returned. The number of files written is unknown.", "集計結果を取得できませんでした。書き込まれたファイル数は不明です。", "服务未返回统计，已写入数量未知。", "無法取得統計結果，已寫入的檔案數未知。"),
        ["IncompleteStatistics"] = new("Complete statistics are unavailable.", "完全な集計結果を取得できませんでした。", "服务未返回完整统计。", "無法取得完整的統計結果。"),
        ["ExtractionFailed"] = new("Extraction failed", "抽出に失敗しました", "提取失败", "擷取失敗"),
        ["PickFailed"] = new("Could not select a folder", "フォルダーを選択できませんでした", "无法选择目录", "無法選擇資料夾"),
        ["OpenFailed"] = new("Could not open the output folder", "出力フォルダーを開けませんでした", "无法打开输出目录", "無法開啟輸出資料夾"),
        ["Images"] = new("Images", "画像", "图片", "圖片"),
        ["GalleryImages"] = new("Gallery images", "ギャラリー画像", "画廊图片", "畫廊圖片"),
        ["Audio"] = new("Audio", "音声", "音频", "音訊"),
        ["GalleryAudio"] = new("Music gallery", "音楽ギャラリー", "音频画廊", "音樂畫廊"),
        ["Video"] = new("Video", "動画", "视频", "影片"),
        ["ScanCompleted"] = new("finished", "終了", "结束", "結束"),
        ["ScanCancelled"] = new("cancelled", "キャンセル", "取消", "取消"),
        ["ScanFailed"] = new("failed", "失敗", "失败", "失敗"),
        ["ScanReport"] = new("Scan: {0} · Files {1}, confirmed archives {2}, unrecognized files {3}, unreadable files {4}, unreadable folders {5}, junctions or links skipped {6}", "スキャン：{0}／ファイル {1}、確認済みアーカイブ {2}、未認識ファイル {3}、読み取り不可のファイル {4}・フォルダー {5}、リンクなどのスキップ {6}", "扫描：{0}；文件 {1}，确认归档 {2}，未识别文件 {3}，不可读文件 {4}，不可读目录 {5}，重解析跳过 {6}", "掃描：{0}；檔案 {1}，確認封存檔 {2}，未辨識檔案 {3}，無法讀取的檔案 {4}、資料夾 {5}，略過連結等 {6}"),
        ["ArchiveReport"] = new("Archives: Opened {0}, failed {1}, not started {2} · Failures {3}", "アーカイブ：開いた数 {0}、失敗 {1}、未処理 {2}／エラー件数 {3}", "归档：打开 {0}，失败 {1}，未开始 {2}；失败事件 {3}", "封存檔：開啟 {0}，失敗 {1}，未開始 {2}；失敗事件 {3}"),
        ["CategoryReport"] = new("{0}: Matched {1}, written {2}, existing files skipped {3}, identification failures {4}, write failures {5}, interrupted items {6}, entries not started {7}, unsupported or unmatched {8}", "{0}：該当 {1}、書き込み済み {2}、既存ファイルのスキップ {3}、識別失敗 {4}、書き込み失敗 {5}、中断 {6}、未処理 {7}、対象外または未対応 {8}", "{0}：匹配 {1}，写入 {2}，已有跳过 {3}，识别失败 {4}，写入失败 {5}，取消中项 {6}，未开始条目工作 {7}，未匹配现有支持 {8}", "{0}：符合 {1}，寫入 {2}，略過既有檔案 {3}，辨識失敗 {4}，寫入失敗 {5}，中止項目 {6}，未開始項目 {7}，不支援或不符合 {8}"),
        ["FailureReport"] = new("Failure: {0} · {1} · Entry {2}\n{3}", "失敗：{0}／{1}／項目 {2}\n{3}", "失败：{0}；{1}；条目 {2}\n{3}", "失敗：{0}；{1}；項目 {2}\n{3}"),
        ["ScanOrOutput"] = new("Scan/output folder", "スキャンまたは出力フォルダー", "扫描/输出目录", "掃描或輸出資料夾"),
        ["GalleryExecutableMissing"] = new("Gallery extraction requires WoH.exe in the Steam game folder.", "ギャラリー抽出にはSteamのゲームフォルダーにあるWoH.exeが必要です。", "画廊提取需要 Steam 游戏目录中的 WoH.exe。", "畫廊擷取需要 Steam 遊戲資料夾中的 WoH.exe。"),
        ["GalleryExecutableUnsafe"] = new("Gallery extraction requires a regular WoH.exe file.", "ギャラリー抽出には通常のWoH.exeファイルが必要です。", "画廊提取要求普通的 WoH.exe 文件路径。", "畫廊擷取需要一般的 WoH.exe 檔案。"),
        ["GalleryExecutableUnverified"] = new("This game version has not been verified. Gallery extraction was skipped.", "このバージョンのゲームは未検証です。ギャラリーの抽出は行われませんでした。", "当前游戏程序版本尚未验证画廊清单；未执行画廊提取。", "此遊戲版本的畫廊清單尚未驗證，未執行畫廊擷取。"),
        ["CancelAndClose"] = new("Cancel extraction and close", "抽出をキャンセルして閉じる", "取消提取并关闭", "取消擷取並關閉"),
        ["CloseTitle"] = new("Confirm close", "終了の確認", "确认关闭", "確認關閉"),
        ["CloseMessage"] = new("Extraction is in progress. Cancel extraction and close?\nCompleted files will be kept. The window will close when extraction has stopped.", "抽出中です。抽出をキャンセルして閉じますか？\n書き込み済みのファイルは残り、抽出が停止してからウィンドウを閉じます。", "提取正在进行。是否取消并关闭？\n已完成文件会保留，窗口将等待提取实际停止。", "擷取正在進行。要取消擷取並關閉嗎？\n已完成的檔案會保留，視窗會在擷取停止後關閉。"),
        ["ImageWarningTitle"] = new("Gallery images are recommended", "ギャラリー画像の抽出をおすすめします", "建议优先提取画廊图片", "建議優先擷取畫廊圖片"),
        ["ImageWarningMessage"] = new("If you mainly want CGs and backgrounds, choose Gallery images. Extracting all images can use about 16 GB of disk space. It took about 40 minutes on the test PC. The time will vary depending on your computer.", "CGや背景が主な目的なら、ギャラリー画像を選んでください。すべての画像を抽出すると、約16 GBの空き容量が必要になる場合があります。テストしたPCでは約40分かかりました。所要時間はPCの性能によって異なります。", "如果你主要想找 CG 和背景，建议选择「画廊图片」。提取全部图片可能占用约 16 GB 空间。在测试电脑上，这大约用了 40 分钟，实际耗时因电脑配置而异。", "如果你主要想找 CG 和背景，建議選擇「畫廊圖片」。擷取全部圖片可能使用約 16 GB 的磁碟空間。在測試電腦上，這大約花了 40 分鐘，實際時間因電腦規格而異。"),
        ["SwitchToGallery"] = new("Extract gallery images instead", "ギャラリー画像に切り替える", "改为提取画廊图片", "改為擷取畫廊圖片"),
        ["ContinueAllImages"] = new("Continue with all images", "すべての画像の抽出を続ける", "继续提取全部图片", "繼續擷取全部圖片"),
        ["Back"] = new("Go back", "戻る", "返回", "返回"),
    };
}
