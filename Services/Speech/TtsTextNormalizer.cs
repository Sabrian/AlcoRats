using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Prepares utterance text for Russian Piper/eSpeak:
/// custom lexicon (built-in + optional JSON) and Latin→Cyrillic phonetics for nicknames.
/// </summary>
public sealed class TtsTextNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILogger<TtsTextNormalizer> _logger;
    private readonly Dictionary<string, string> _lexicon;
    private readonly string _lexiconPath;

    public TtsTextNormalizer(ILogger<TtsTextNormalizer> logger = null, string lexiconPath = null)
    {
        _logger = logger ?? NullLogger<TtsTextNormalizer>.Instance;
        _lexiconPath = lexiconPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlcoRats",
                "tts-lexicon.json");

        _lexicon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in BuiltInLexicon)
            _lexicon[key] = value;

        LoadUserLexicon();
    }

    public string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var result = text.Trim();

        // Whole-phrase / multi-word replacements first (longest keys win).
        foreach (var (from, to) in _lexicon.OrderByDescending(kv => kv.Key.Length))
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                continue;
            result = Regex.Replace(
                result,
                $@"\b{Regex.Escape(from)}\b",
                to,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Remaining Latin tokens → approximate Russian phonetics for eSpeak/Piper.
        result = Regex.Replace(
            result,
            @"\b[A-Za-z][A-Za-z0-9_\-']*\b",
            m => TransliterateLatinToken(m.Value));

        return Regex.Replace(result, @"\s+", " ").Trim();
    }

    private void LoadUserLexicon()
    {
        try
        {
            if (!File.Exists(_lexiconPath))
            {
                EnsureDefaultUserLexiconFile();
                return;
            }

            var json = File.ReadAllText(_lexiconPath);
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            if (entries is null)
                return;

            // Extend only — do not override built-in (stale seed files used to pin wrong stress,
            // e.g. Bozon). Users can still add new nicknames not present in BuiltInLexicon.
            var added = 0;
            foreach (var (key, value) in entries)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;
                var k = key.Trim();
                if (_lexicon.ContainsKey(k))
                    continue;
                _lexicon[k] = value.Trim();
                added++;
            }

            _logger.LogInformation(
                "TTS lexicon extended (+{Added} user entries, {Count} total) from {Path}",
                added,
                _lexicon.Count,
                _lexiconPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load TTS lexicon from {Path}", _lexiconPath);
        }
    }

    private void EnsureDefaultUserLexiconFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(_lexiconPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(_lexiconPath))
                return;

            // Seed file so the user can extend pronunciations without code changes.
            var seed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in BuiltInLexicon)
                seed[key] = value;
            File.WriteAllText(_lexiconPath, JsonSerializer.Serialize(seed, JsonOptions));
            _logger.LogInformation("Created TTS lexicon template at {Path}", _lexiconPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not create TTS lexicon template");
        }
    }

    private static string TransliterateLatinToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return token;

        // Keep pure numbers as-is.
        if (token.All(char.IsDigit))
            return token;

        var sb = new StringBuilder(token.Length + 4);
        var lower = token.ToLowerInvariant();
        for (var i = 0; i < lower.Length;)
        {
            // Multi-letter digraphs first.
            if (Match(lower, i, "sch", out var n) || Match(lower, i, "shch", out n))
            {
                sb.Append("щ");
                i += n;
                continue;
            }
            if (Match(lower, i, "yo", out n)) { sb.Append("ё"); i += n; continue; }
            if (Match(lower, i, "yu", out n)) { sb.Append("ю"); i += n; continue; }
            if (Match(lower, i, "ya", out n)) { sb.Append("я"); i += n; continue; }
            if (Match(lower, i, "zh", out n)) { sb.Append("ж"); i += n; continue; }
            if (Match(lower, i, "kh", out n)) { sb.Append("х"); i += n; continue; }
            if (Match(lower, i, "ts", out n)) { sb.Append("ц"); i += n; continue; }
            if (Match(lower, i, "ch", out n)) { sb.Append("ч"); i += n; continue; }
            if (Match(lower, i, "sh", out n)) { sb.Append("ш"); i += n; continue; }
            if (Match(lower, i, "th", out n)) { sb.Append("т"); i += n; continue; }
            if (Match(lower, i, "ph", out n)) { sb.Append("ф"); i += n; continue; }
            if (Match(lower, i, "qu", out n)) { sb.Append("кв"); i += n; continue; }

            sb.Append(MapLatinChar(lower[i]));
            i++;
        }

        return sb.ToString();
    }

    private static bool Match(string s, int index, string digraph, out int length)
    {
        length = digraph.Length;
        return index + length <= s.Length
            && string.Compare(s, index, digraph, 0, length, StringComparison.Ordinal) == 0;
    }

    private static string MapLatinChar(char ch) => ch switch
    {
        'a' => "а",
        'b' => "б",
        'c' => "к",
        'd' => "д",
        'e' => "е",
        'f' => "ф",
        'g' => "г",
        'h' => "х",
        'i' => "и",
        'j' => "дж",
        'k' => "к",
        'l' => "л",
        'm' => "м",
        'n' => "н",
        'o' => "о",
        'p' => "п",
        'q' => "к",
        'r' => "р",
        's' => "с",
        't' => "т",
        'u' => "у",
        'v' => "в",
        'w' => "в",
        'x' => "кс",
        'y' => "й",
        'z' => "з",
        '_' or '-' or '\'' => " ",
        _ => char.IsDigit(ch) ? ch.ToString() : string.Empty
    };

    /// <summary>
    /// Built-in pronunciation hints: conference nicknames plus common internet/IT anglicisms
    /// (dvach-style lexicon). Stress = combining acute U+0301 after the stressed vowel (Piper/RU).
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> BuiltInLexicon
    {
        get
        {
            // --- nicknames / product ---
            // Bozon: Бо-зо́н — stress on 2nd syllable (U+0301 after 2nd «о»).
            yield return new("Bozon", "Бозо\u0301н");
            yield return new("Бозон", "Бозо\u0301н");
            yield return new("бозон", "Бозо\u0301н");
            yield return new("Tommy", "То\u0301мми");
            yield return new("Tommie", "То\u0301мми");
            yield return new("Sting", "Стинг");
            yield return new("Jugurda", "Джугурда\u0301");
            yield return new("Comrade", "Това\u0301рищ");
            yield return new("Zayatz", "За\u0301яц");
            yield return new("BOT", "бот");
            yield return new("AlcoRats", "А\u0301лко Рэ\u0301тс");
            yield return new("Alco Rats", "А\u0301лко Рэ\u0301тс");
            yield return new("Jitsi", "Джи\u0301тси");
            yield return new("OK", "оке\u0301й");
            yield return new("Discord", "Диско\u0301рд");
            yield return new("дискорд", "диско\u0301рд");

            // --- IT / web / devops (latin → stressed cyrillic) ---
            yield return new("API", "э\u0301й пи а\u0301й");
            yield return new("URL", "ю ар э\u0301л");
            yield return new("URI", "ю ар и\u0301");
            yield return new("HTTP", "эйч ти ти пи\u0301");
            yield return new("HTTPS", "эйч ти ти пи э\u0301с");
            yield return new("HTML", "эйч ти эм э\u0301л");
            yield return new("CSS", "си эс э\u0301с");
            yield return new("JSON", "дже\u0301йсон");
            yield return new("XML", "экс эм э\u0301л");
            yield return new("SQL", "эс кью э\u0301л");
            yield return new("NoSQL", "ноу эс кью э\u0301л");
            yield return new("SDK", "эс ди кэ\u0301й");
            yield return new("CLI", "си эл а\u0301й");
            yield return new("GUI", "гу\u0301и");
            yield return new("IDE", "ай ди и\u0301");
            yield return new("CPU", "си пи ю\u0301");
            yield return new("GPU", "джи пи ю\u0301");
            yield return new("RAM", "рэм");
            yield return new("SSD", "эс эс ди\u0301");
            yield return new("HDD", "эйч ди ди\u0301");
            yield return new("USB", "ю эс би\u0301");
            yield return new("WiFi", "ва\u0301й фай");
            yield return new("Wi-Fi", "ва\u0301й фай");
            yield return new("Bluetooth", "блуту\u0301т");
            yield return new("VPN", "ви пи э\u0301н");
            yield return new("DNS", "ди эн э\u0301с");
            yield return new("CDN", "си ди э\u0301н");
            yield return new("IP", "ай пи\u0301");
            yield return new("TCP", "ти си пи\u0301");
            yield return new("UDP", "ю ди пи\u0301");
            yield return new("SSH", "эс эс э\u0301йч");
            yield return new("FTP", "эф ти пи\u0301");
            yield return new("SMTP", "эс эм ти пи\u0301");
            yield return new("JWT", "джей да\u0301бл ю ти");
            yield return new("OAuth", "о\u0301аут");
            yield return new("UUID", "ю ю ай ди\u0301");
            yield return new("GUID", "гуи\u0301д");
            yield return new("REST", "рэст");
            yield return new("GraphQL", "гра\u0301ф кью эл");
            yield return new("gRPC", "джи а\u0301р пи си");
            yield return new("WebSocket", "вебсо\u0301кет");
            yield return new("websocket", "вебсо\u0301кет");
            yield return new("backend", "бэ\u0301кэнд");
            yield return new("frontend", "фро\u0301нтэнд");
            yield return new("fullstack", "фу\u0301лстэк");
            yield return new("full-stack", "фу\u0301лстэк");
            yield return new("devops", "де\u0301вопс");
            yield return new("DevOps", "де\u0301вопс");
            yield return new("deploy", "депло\u0301й");
            yield return new("deployment", "депло\u0301ймент");
            yield return new("rollback", "ро\u0301ллбэк");
            yield return new("hotfix", "хо\u0301тфикс");
            yield return new("bugfix", "багфи\u0301кс");
            yield return new("commit", "коми\u0301т");
            yield return new("commits", "коми\u0301ты");
            yield return new("merge", "мёрдж");
            yield return new("rebase", "рибе\u0301йз");
            yield return new("pull request", "пул рекве\u0301ст");
            yield return new("Pull Request", "пул рекве\u0301ст");
            yield return new("pullrequest", "пулрекве\u0301ст");
            yield return new("PR", "пи а\u0301р");
            yield return new("branch", "бранч");
            yield return new("checkout", "че\u0301каут");
            yield return new("push", "пуш");
            yield return new("clone", "клоун");
            yield return new("fork", "фо\u0301рк");
            yield return new("repo", "ре\u0301по");
            yield return new("repository", "репозито\u0301рий");
            yield return new("pipeline", "па\u0301йплайн");
            yield return new("CI", "си а\u0301й");
            yield return new("CD", "си ди\u0301");
            yield return new("container", "конте\u0301йнер");
            yield return new("docker", "до\u0301кер");
            yield return new("Docker", "до\u0301кер");
            yield return new("kubernetes", "куберне\u0301тис");
            yield return new("Kubernetes", "куберне\u0301тис");
            yield return new("k8s", "ку\u0301берс");
            yield return new("kubectl", "кубконтро\u0301л");
            yield return new("helm", "хелм");
            yield return new("terraform", "те\u0301рраформ");
            yield return new("ansible", "э\u0301нсибл");
            yield return new("nginx", "э\u0301нджинкс");
            yield return new("Nginx", "э\u0301нджинкс");
            yield return new("apache", "апа\u0301ч");
            yield return new("Apache", "апа\u0301ч");
            yield return new("linux", "ли\u0301нукс");
            yield return new("Linux", "ли\u0301нукс");
            yield return new("ubuntu", "убу\u0301нту");
            yield return new("Ubuntu", "убу\u0301нту");
            yield return new("debian", "де\u0301биан");
            yield return new("macos", "мак о\u0301эс");
            yield return new("macOS", "мак о\u0301эс");
            yield return new("iOS", "а\u0301й о\u0301эс");
            yield return new("android", "андро\u0301ид");
            yield return new("Android", "андро\u0301ид");
            yield return new("windows", "ви\u0301ндоус");
            yield return new("Windows", "ви\u0301ндоус");
            yield return new("python", "па\u0301йтон");
            yield return new("Python", "па\u0301йтон");
            yield return new("javascript", "джаваскри\u0301пт");
            yield return new("JavaScript", "джаваскри\u0301пт");
            yield return new("typescript", "тайпскри\u0301пт");
            yield return new("TypeScript", "тайпскри\u0301пт");
            yield return new("golang", "го\u0301лэнг");
            yield return new("kotlin", "ко\u0301тлин");
            yield return new("Kotlin", "ко\u0301тлин");
            yield return new("swift", "свифт");
            yield return new("rust", "раст");
            yield return new("Rust", "раст");
            yield return new("csharp", "си ша\u0301рп");
            yield return new("C#", "си ша\u0301рп");
            yield return new("dotnet", "дот нэт");
            yield return new(".NET", "дот нэт");
            yield return new("nodejs", "но\u0301уд джэйэс");
            yield return new("Node.js", "но\u0301уд джэйэс");
            yield return new("npm", "эн пи э\u0301м");
            yield return new("yarn", "ярн");
            yield return new("webpack", "вебпа\u0301к");
            yield return new("react", "реа\u0301кт");
            yield return new("React", "реа\u0301кт");
            yield return new("vue", "вью");
            yield return new("Vue", "вью");
            yield return new("angular", "а\u0301нгьюлар");
            yield return new("Angular", "а\u0301нгьюлар");
            yield return new("svelte", "свэлт");
            yield return new("avalonia", "авало\u0301ния");
            yield return new("Avalonia", "авало\u0301ния");
            yield return new("framework", "фре\u0301ймворк");
            yield return new("library", "ла\u0301йбрери");
            yield return new("plugin", "пла\u0301гин");
            yield return new("middleware", "ми\u0301ддлвэр");
            yield return new("endpoint", "э\u0301ндпоинт");
            yield return new("payload", "пе\u0301йлоад");
            yield return new("header", "хе\u0301дер");
            yield return new("headers", "хе\u0301деры");
            yield return new("cookie", "ку\u0301ки");
            yield return new("cookies", "ку\u0301ки");
            yield return new("cache", "кэш");
            yield return new("caching", "кэ\u0301шинг");
            yield return new("token", "то\u0301кен");
            yield return new("tokens", "то\u0301кены");
            yield return new("session", "се\u0301ссия");
            yield return new("auth", "о\u0301т");
            yield return new("login", "ло\u0301гин");
            yield return new("logout", "ло\u0301гаут");
            yield return new("signup", "са\u0301йнап");
            yield return new("sign-up", "са\u0301йнап");
            yield return new("password", "па\u0301ссворд");
            yield return new("username", "ю\u0301зернейм");
            yield return new("nickname", "ни\u0301кнейм");
            yield return new("nick", "ник");
            yield return new("admin", "а\u0301дмин");
            yield return new("moderator", "модера\u0301тор");
            yield return new("mod", "мод");
            yield return new("ban", "бан");
            yield return new("unban", "анба\u0301н");
            yield return new("timeout", "та\u0301ймаут");
            yield return new("deadline", "де\u0301длайн");
            yield return new("milestone", "ма\u0301йлстоун");
            yield return new("sprint", "спринт");
            yield return new("scrum", "скрам");
            yield return new("kanban", "канба\u0301н");
            yield return new("backlog", "бэкло\u0301г");
            yield return new("standup", "стэ\u0301ндап");
            yield return new("stand-up", "стэ\u0301ндап");
            yield return new("meeting", "ми\u0301тинг");
            yield return new("call", "колл");
            yield return new("softphone", "со\u0301фтфон");
            yield return new("headset", "хе\u0301дсет");
            yield return new("mic", "мик");
            yield return new("microphone", "микрофо\u0301н");
            yield return new("webcam", "вебка\u0301м");
            yield return new("stream", "стрим");
            yield return new("streaming", "стри\u0301минг");
            yield return new("streamer", "стри\u0301мер");
            yield return new("podcast", "по\u0301дкаст");
            yield return new("webinar", "вебина\u0301р");
            yield return new("online", "онла\u0301йн");
            yield return new("offline", "офла\u0301йн");
            yield return new("update", "а\u0301пдейт");
            yield return new("upgrade", "а\u0301пгрейд");
            yield return new("downgrade", "да\u0301унгрейд");
            yield return new("download", "да\u0301унлоад");
            yield return new("upload", "а\u0301плоад");
            yield return new("install", "инста\u0301лл");
            yield return new("uninstall", "анинста\u0301лл");
            yield return new("setup", "се\u0301тап");
            yield return new("config", "ко\u0301нфиг");
            yield return new("settings", "се\u0301ттинги");
            yield return new("default", "дефо\u0301лт");
            yield return new("feature", "фи\u0301ча");
            yield return new("features", "фи\u0301чи");
            yield return new("bug", "баг");
            yield return new("bugs", "баги\u0301");
            yield return new("crash", "крэш");
            yield return new("lag", "лаг");
            yield return new("ping", "пинг");
            yield return new("fps", "эф пи э\u0301с");
            yield return new("bitrate", "битре\u0301йт");
            yield return new("latency", "ле\u0301тенси");
            yield return new("bandwidth", "бэ\u0301ндвидс");
            yield return new("proxy", "про\u0301кси");
            yield return new("firewall", "фа\u0301ервол");
            yield return new("malware", "ма\u0301лварь");
            yield return new("ransomware", "ра\u0301нсомварь");
            yield return new("phishing", "фи\u0301шинг");
            yield return new("spam", "спам");
            yield return new("spoiler", "спо\u0301йлер");
            yield return new("screenshot", "скриншо\u0301т");
            yield return new("screen", "скрин");
            yield return new("desktop", "де\u0301сктоп");
            yield return new("laptop", "лэ\u0301птоп");
            yield return new("smartphone", "смартфо\u0301н");
            yield return new("tablet", "тэ\u0301блет");
            yield return new("browser", "бра\u0301узер");
            yield return new("tab", "таб");
            yield return new("link", "линк");
            yield return new("hyperlink", "ха\u0301йперлинк");
            yield return new("hashtag", "хэште\u0301г");
            yield return new("tag", "тэг");
            yield return new("thread", "трэд");
            yield return new("post", "пост");
            yield return new("repost", "репо\u0301ст");
            yield return new("retweet", "ритви\u0301т");
            yield return new("like", "лайк");
            yield return new("dislike", "дисла\u0301йк");
            yield return new("follow", "фо\u0301ллоу");
            yield return new("follower", "фо\u0301лловер");
            yield return new("followers", "фо\u0301лловеры");
            yield return new("subscribe", "сабска\u0301йб");
            yield return new("subscriber", "сабска\u0301йбер");
            yield return new("unsubscribe", "ансабска\u0301йб");
            yield return new("feed", "фид");
            yield return new("timeline", "та\u0301ймлайн");
            yield return new("story", "сто\u0301ри");
            yield return new("stories", "сто\u0301рис");
            yield return new("reels", "рилс");
            yield return new("shorts", "шо\u0301ртс");
            yield return new("content", "ко\u0301нтент");
            yield return new("trending", "тре\u0301ндинг");
            yield return new("viral", "ва\u0301йрал");
            yield return new("hype", "хайп");
            yield return new("meme", "мим");
            yield return new("memes", "ми\u0301мы");
            yield return new("lol", "лол");
            yield return new("kek", "кек");
            yield return new("lmao", "эл ма\u0301о");
            yield return new("omg", "о эм джи\u0301");
            yield return new("wtf", "ват зэ фа\u0301к");
            yield return new("imho", "и\u0301мхо");
            yield return new("afaik", "афе\u0301йк");
            yield return new("tl;dr", "ти эл ди а\u0301р");
            yield return new("tldr", "ти эл ди а\u0301р");
            yield return new("nsfw", "эн эс эф да\u0301бл ю");
            yield return new("sfw", "эс эф да\u0301бл ю");
            yield return new("cringe", "кринж");
            yield return new("based", "бе\u0301йзд");
            yield return new("cope", "ко\u0301уп");
            yield return new("seethe", "си\u0301з");
            yield return new("ratio", "ре\u0301йшио");
            yield return new("skill", "скилл");
            yield return new("skill issue", "скилл и\u0301шью");
            yield return new("noob", "нуб");
            yield return new("newbie", "ню\u0301би");
            yield return new("pro", "про");
            yield return new("tryhard", "тра\u0301йхард");
            yield return new("afk", "эй эф кэ\u0301й");
            yield return new("brb", "би ар би\u0301");
            yield return new("gg", "джи джи\u0301");
            yield return new("wp", "да\u0301бл ю пи");
            yield return new("ez", "и\u0301зи");
            yield return new("easy", "и\u0301зи");
            yield return new("hardcore", "хардко\u0301р");
            yield return new("softcore", "софтко\u0301р");
            yield return new("gameplay", "ге\u0301ймплей");
            yield return new("gamer", "ге\u0301ймер");
            yield return new("gaming", "ге\u0301йминг");
            yield return new("esports", "киберспо\u0301рт");
            yield return new("e-sports", "киберспо\u0301рт");
            yield return new("frag", "фраг");
            yield return new("headshot", "хедшо\u0301т");
            yield return new("clutch", "клатч");
            yield return new("respawn", "респа\u0301ун");
            yield return new("spawn", "спа\u0301ун");
            yield return new("loot", "лут");
            yield return new("lootbox", "лутбо\u0301кс");
            yield return new("skin", "скин");
            yield return new("skins", "ски\u0301ны");
            yield return new("DLC", "ди эл си\u0301");
            yield return new("patch", "патч");
            yield return new("nerf", "нерф");
            yield return new("buff", "бафф");
            yield return new("meta", "ме\u0301та");
            yield return new("OP", "оу пи\u0301");
            yield return new("overpowered", "о\u0301верпауэрд");
            yield return new("underpowered", "а\u0301ндерпауэрд");
            yield return new("boss", "босс");
            yield return new("raid", "рейд");
            yield return new("quest", "квест");
            yield return new("NPC", "эн пи си\u0301");
            yield return new("PvP", "пи ви пи\u0301");
            yield return new("PvE", "пи ви и\u0301");
            yield return new("MMORPG", "эм эм оу ар пи джи\u0301");
            yield return new("RPG", "ар пи джи\u0301");
            yield return new("RTS", "ар ти э\u0301с");
            yield return new("MOBA", "мо\u0301ба");
            yield return new("battle royale", "ба\u0301ттл роя\u0301ль");
            yield return new("chat", "чат");
            yield return new("voicechat", "во\u0301йсчат");
            yield return new("voice chat", "во\u0301йс чат");
            yield return new("DM", "ди э\u0301м");
            yield return new("PM", "пи э\u0301м");
            yield return new("DMCA", "ди эм си э\u0301й");
            yield return new("copyright", "ко\u0301пирайт");
            yield return new("fair use", "фэ\u0301р юз");
            yield return new("open source", "о\u0301упен со\u0301рс");
            yield return new("opensource", "о\u0301упенсорс");
            yield return new("freeware", "фри\u0301вэр");
            yield return new("shareware", "шэ\u0301рвэр");
            yield return new("adware", "э\u0301двар");
            yield return new("startup", "ста\u0301ртап");
            yield return new("start-up", "ста\u0301ртап");
            yield return new("freelancer", "фрила\u0301нсер");
            yield return new("freelance", "фрила\u0301нс");
            yield return new("outsourcing", "а\u0301утсорсинг");
            yield return new("outsource", "а\u0301утсорс");
            yield return new("offshore", "офшо\u0301р");
            yield return new("manager", "ме\u0301неджер");
            yield return new("management", "ме\u0301неджмент");
            yield return new("marketing", "ма\u0301ркетинг");
            yield return new("branding", "брэ\u0301ндинг");
            yield return new("brand", "брэнд");
            yield return new("promo", "промо\u0301");
            yield return new("promotion", "промо\u0301ушен");
            yield return new("sales", "се\u0301йлз");
            yield return new("sale", "сейл");
            yield return new("cashback", "кэшбэ\u0301к");
            yield return new("cash", "кэш");
            yield return new("billing", "би\u0301ллинг");
            yield return new("invoice", "и\u0301нвойс");
            yield return new("cart", "карт");
            yield return new("wishlist", "ви\u0301шлист");
            yield return new("shop", "шоп");
            yield return new("shopping", "шо\u0301ппинг");
            yield return new("marketplace", "ма\u0301ркетплейс");
            yield return new("e-commerce", "и\u0301 коммёрс");
            yield return new("ecommerce", "и\u0301коммёрс");
            yield return new("crypto", "кри\u0301пто");
            yield return new("bitcoin", "би\u0301ткоин");
            yield return new("Bitcoin", "би\u0301ткоин");
            yield return new("blockchain", "блокче\u0301йн");
            yield return new("NFT", "эн эф ти\u0301");
            yield return new("tokenomics", "токено\u0301микс");
            yield return new("wallet", "во\u0301ллет");
            yield return new("mining", "ма\u0301йнинг");
            yield return new("miner", "ма\u0301йнер");
            yield return new("hodl", "хо\u0301дл");
            yield return new("fomo", "фо\u0301мо");
            yield return new("fud", "фад");
            yield return new("airdrop", "э\u0301ирдроп");
            yield return new("whitelist", "ва\u0301йтлист");
            yield return new("blacklist", "блэкли\u0301ст");
            yield return new("greylist", "гре\u0301йлист");
            yield return new("captcha", "ка\u0301пча");
            yield return new("botfarm", "ботфа\u0301рма");
            yield return new("botnet", "ботне\u0301т");
            yield return new("ddos", "ди до\u0301с");
            yield return new("DDoS", "ди до\u0301с");
            yield return new("exploit", "экспло\u0301йт");
            yield return new("zero-day", "зи\u0301ро дей");
            yield return new("zeroday", "зи\u0301родей");
            yield return new("patch notes", "патчно\u0301утс");
            yield return new("changelog", "че\u0301йнджлог");
            yield return new("README", "ри\u0301дми");
            yield return new("license", "ла\u0301йсенс");
            yield return new("licence", "ла\u0301йсенс");
            yield return new("terms", "тёрмс");
            yield return new("privacy", "пра\u0301йвеси");
            yield return new("GDPR", "джи ди пи а\u0301р");
            yield return new("cookie policy", "ку\u0301ки по\u0301лиси");
            yield return new("newsletter", "нью\u0301слеттер");
            yield return new("mailing", "ме\u0301йлинг");
            yield return new("email", "и\u0301мейл");
            yield return new("e-mail", "и\u0301мейл");
            yield return new("inbox", "и\u0301нбокс");
            yield return new("outbox", "а\u0301утбокс");
            yield return new("attachment", "атта\u0301чмент");
            yield return new("forward", "фо\u0301рвард");
            yield return new("reply", "рипла\u0301й");
            yield return new("cc", "си си\u0301");
            yield return new("bcc", "би си си\u0301");
            yield return new("calendar", "календа\u0301рь");
            yield return new("scheduler", "ске\u0301джулер");
            yield return new("reminder", "рима\u0301йндер");
            yield return new("notification", "нотифика\u0301ция");
            yield return new("notifications", "нотифика\u0301ции");
            yield return new("alert", "але\u0301рт");
            yield return new("popup", "по\u0301пап");
            yield return new("pop-up", "по\u0301пап");
            yield return new("modal", "мо\u0301дал");
            yield return new("tooltip", "ту\u0301лтип");
            yield return new("dropdown", "дро\u0301пдаун");
            yield return new("sidebar", "са\u0301йдбар");
            yield return new("navbar", "навба\u0301р");
            yield return new("footer", "фу\u0301тер");
            yield return new("layout", "ле\u0301йаут");
            yield return new("responsive", "респо\u0301нсив");
            yield return new("adaptive", "ада\u0301птив");
            yield return new("UI", "ю а\u0301й");
            yield return new("UX", "ю э\u0301кс");
            yield return new("UX/UI", "ю экс ю а\u0301й");
            yield return new("wireframe", "ва\u0301йрфрейм");
            yield return new("mockup", "мо\u0301кап");
            yield return new("prototype", "прототи\u0301п");
            yield return new("MVP", "эм ви пи\u0301");
            yield return new("KPI", "кэй пи а\u0301й");
            yield return new("ROI", "ар оу а\u0301й");
            yield return new("OKR", "оу кэй а\u0301р");
            yield return new("B2B", "би ту би\u0301");
            yield return new("B2C", "би ту си\u0301");
            yield return new("SaaS", "са\u0301ас");
            yield return new("PaaS", "па\u0301ас");
            yield return new("IaaS", "айа\u0301ас");
            yield return new("cloud", "кла\u0301уд");
            yield return new("serverless", "сё\u0301рверлес");
            yield return new("microservice", "микросе\u0301рвис");
            yield return new("microservices", "микросе\u0301рвисы");
            yield return new("monolith", "мо\u0301нолит");
            yield return new("legacy", "ле\u0301гаси");
            yield return new("refactor", "рефа\u0301ктор");
            yield return new("refactoring", "рефа\u0301кторинг");
            yield return new("code review", "ко\u0301д ревью\u0301");
            yield return new("codereview", "кодревью\u0301");
            yield return new("linter", "ли\u0301нтер");
            yield return new("lint", "линт");
            yield return new("formatter", "фо\u0301рматтер");
            yield return new("unit test", "ю\u0301нит тест");
            yield return new("unittest", "юните\u0301ст");
            yield return new("integration test", "интегре\u0301йшн тест");
            yield return new("e2e", "и ту и\u0301");
            yield return new("coverage", "ка\u0301веридж");
            yield return new("mock", "мок");
            yield return new("stub", "стаб");
            yield return new("fixture", "фи\u0301ксча");
            yield return new("debug", "деба\u0301г");
            yield return new("debugger", "деба\u0301ггер");
            yield return new("breakpoint", "бре\u0301йкпоинт");
            yield return new("stacktrace", "стэктре\u0301йс");
            yield return new("stack trace", "стэк тре\u0301йс");
            yield return new("exception", "эксе\u0301пшн");
            yield return new("error", "э\u0301ррор");
            yield return new("warning", "во\u0301рнинг");
            yield return new("info", "и\u0301нфо");
            yield return new("verbose", "вербо\u0301уз");
            yield return new("logfile", "ло\u0301гфайл");
            yield return new("logging", "ло\u0301ггинг");
            yield return new("monitor", "мо\u0301нитор");
            yield return new("monitoring", "монито\u0301ринг");
            yield return new("metrics", "ме\u0301трикс");
            yield return new("dashboard", "да\u0301шборд");
            yield return new("analytics", "ана\u0301литикс");
            yield return new("tracking", "трэ\u0301кинг");
            yield return new("pixel", "пи\u0301ксель");
            yield return new("banner", "ба\u0301ннер");
            yield return new("landing", "лэ\u0301ндинг");
            yield return new("landing page", "лэ\u0301ндинг пе\u0301йдж");
            yield return new("CTA", "си ти э\u0301й");
            yield return new("SEO", "си и о\u0301у");
            yield return new("SEM", "си и э\u0301м");
            yield return new("SMM", "эс эм э\u0301м");
            yield return new("influencer", "инфлю\u0301енсер");
            yield return new("blogger", "бло\u0301ггер");
            yield return new("vlogger", "вло\u0301ггер");
            yield return new("blog", "блог");
            yield return new("vlog", "влог");
            yield return new("tiktok", "тикто\u0301к");
            yield return new("TikTok", "тикто\u0301к");
            yield return new("instagram", "инстагра\u0301м");
            yield return new("Instagram", "инстагра\u0301м");
            yield return new("youtube", "юту\u0301б");
            yield return new("YouTube", "юту\u0301б");
            yield return new("twitter", "тви\u0301ттер");
            yield return new("Twitter", "тви\u0301ттер");
            yield return new("facebook", "фе\u0301йсбук");
            yield return new("Facebook", "фе\u0301йсбук");
            yield return new("telegram", "телегра\u0301м");
            yield return new("Telegram", "телегра\u0301м");
            yield return new("whatsapp", "вотса\u0301п");
            yield return new("WhatsApp", "вотса\u0301п");
            yield return new("reddit", "ре\u0301ддит");
            yield return new("Reddit", "ре\u0301ддит");
            yield return new("twitch", "твич");
            yield return new("Twitch", "твич");
            yield return new("spotify", "спо\u0301тифай");
            yield return new("Spotify", "спо\u0301тифай");
            yield return new("netflix", "не\u0301тфликс");
            yield return new("Netflix", "не\u0301тфликс");
            yield return new("google", "гу\u0301гл");
            yield return new("Google", "гу\u0301гл");
            yield return new("chrome", "хром");
            yield return new("Chrome", "хром");
            yield return new("firefox", "фа\u0301ерфокс");
            yield return new("Firefox", "фа\u0301ерфокс");
            yield return new("safari", "сафа\u0301ри");
            yield return new("Safari", "сафа\u0301ри");
            yield return new("edge", "эдж");
            yield return new("Edge", "эдж");
            yield return new("github", "ги\u0301тхаб");
            yield return new("GitHub", "ги\u0301тхаб");
            yield return new("gitlab", "ги\u0301тлаб");
            yield return new("GitLab", "ги\u0301тлаб");
            yield return new("bitbucket", "би\u0301тбакет");
            yield return new("stackoverflow", "стэ\u0301к о\u0301верфлоу");
            yield return new("StackOverflow", "стэ\u0301к о\u0301верфлоу");
            yield return new("wikipedia", "википе\u0301дия");
            yield return new("Wikipedia", "википе\u0301дия");
            yield return new("wiki", "ви\u0301ки");
            yield return new("lurkmore", "луркмо\u0301рье");
            yield return new("dvach", "двач");
            yield return new("2ch", "двач");
            yield return new("imageboard", "и\u0301миджборд");
            yield return new("board", "борд");
            yield return new("op", "о\u0301п");
            yield return new("sage", "са\u0301ж");
            yield return new("bump", "бамп");
            yield return new("tripcode", "трипко\u0301д");
            yield return new("shitpost", "шитпо\u0301ст");
            yield return new("copypasta", "копипа\u0301ста");
            yield return new("copypaste", "копипе\u0301йст");
            yield return new("copy-paste", "ко\u0301пи пе\u0301йст");
            yield return new("troll", "тролль");
            yield return new("trolling", "тро\u0301ллинг");
            yield return new("flame", "фле\u0301йм");
            yield return new("flamewar", "фле\u0301ймвар");
            yield return new("offtopic", "офто\u0301пик");
            yield return new("off-topic", "офто\u0301пик");
            yield return new("on-topic", "онто\u0301пик");
            yield return new("trigger", "три\u0301ггер");
            yield return new("triggered", "три\u0301ггерд");
            yield return new("safe space", "се\u0301йф спе\u0301йс");
            yield return new("cancel", "кэ\u0301нсел");
            yield return new("cancel culture", "кэ\u0301нсел ка\u0301лча");
            yield return new("woke", "во\u0301ук");
            yield return new("redpill", "редпи\u0301лл");
            yield return new("bluepill", "блюпи\u0301лл");
            yield return new("blackpill", "блэкпи\u0301лл");
            yield return new("simp", "симп");
            yield return new("incel", "и\u0301нсел");
            yield return new("normie", "но\u0301рми");
            yield return new("boomer", "бу\u0301мер");
            yield return new("zoomer", "зу\u0301мер");
            yield return new("millennial", "миллениа\u0301л");
            yield return new("gen z", "джен зи\u0301");
            yield return new("main character", "ме\u0301йн ка\u0301рактер");
            yield return new("sidequest", "са\u0301йдквест");
            yield return new("lore", "лор");
            yield return new("canon", "ка\u0301нон");
            yield return new("fanon", "фа\u0301нон");
            yield return new("fanfic", "фанфи\u0301к");
            yield return new("fanfiction", "фанфи\u0301кшн");
            yield return new("cosplay", "коспле\u0301й");
            yield return new("cosplayer", "коспле\u0301ер");
            yield return new("anime", "аниме\u0301");
            yield return new("manga", "ма\u0301нга");
            yield return new("otaku", "ота\u0301ку");
            yield return new("weeb", "виб");
            yield return new("waifu", "ва\u0301йфу");
            yield return new("husbando", "хасба\u0301ндо");
            yield return new("senpai", "сэмпа\u0301й");
            yield return new("kawaii", "кава\u0301йи");
            yield return new("sugoi", "суго\u0301й");
            yield return new("baka", "ба\u0301ка");
            yield return new("desu", "дэ\u0301су");
            yield return new("owo", "о\u0301у во\u0301у");
            yield return new("uwu", "у\u0301 ву\u0301");
            yield return new("poggers", "по\u0301ггерс");
            yield return new("pog", "пог");
            yield return new("kekw", "кек да\u0301бл ю");
            yield return new("pepe", "пепе\u0301");
            yield return new("wojak", "во\u0301як");
            yield return new("chad", "чад");
            yield return new("virgin", "вё\u0301рджин");
            yield return new("gigachad", "гигача\u0301д");
            yield return new("sigma", "си\u0301гма");
            yield return new("alpha", "а\u0301льфа");
            yield return new("beta", "бе\u0301та");
            yield return new("gamma", "га\u0301мма");
            yield return new("delta", "де\u0301льта");
            yield return new("omega", "оме\u0301га");
            yield return new("flex", "флекс");
            yield return new("flexing", "фле\u0301ксинг");
            yield return new("drip", "дрип");
            yield return new("fit", "фит");
            yield return new("outfit", "а\u0301утфит");
            yield return new("look", "лук");
            yield return new("style", "стайл");
            yield return new("trend", "тренд");
            yield return new("vibe", "вайб");
            yield return new("vibes", "ва\u0301йбы");
            yield return new("mood", "муд");
            yield return new("energy", "э\u0301нерджи");
            yield return new("chill", "чилл");
            yield return new("relax", "рила\u0301кс");
            yield return new("stress", "стрэсс");
            yield return new("burnout", "бёрна\u0301ут");
            yield return new("overtime", "о\u0301вертайм");
            yield return new("remote", "римо\u0301ут");
            yield return new("hybrid", "ха\u0301йбрид");
            yield return new("office", "о\u0301фис");
            yield return new("coworking", "ко\u0301воркинг");
            yield return new("team", "тим");
            yield return new("teambuilding", "тимби\u0301лдинг");
            yield return new("team lead", "тимли\u0301д");
            yield return new("teamlead", "тимли\u0301д");
            yield return new("tech lead", "техли\u0301д");
            yield return new("techlead", "техли\u0301д");
            yield return new("CTO", "си ти о\u0301у");
            yield return new("CEO", "си и о\u0301у");
            yield return new("CFO", "си эф о\u0301у");
            yield return new("HR", "эйч а\u0301р");
            yield return new("PO", "пи о\u0301у");
            yield return new("QA", "кью э\u0301й");
            yield return new("QC", "кью си\u0301");
            yield return new("junior", "джу\u0301ниор");
            yield return new("middle", "ми\u0301ддл");
            yield return new("senior", "се\u0301ниор");
            yield return new("lead", "лид");
            yield return new("intern", "и\u0301нтерн");
            yield return new("internship", "и\u0301нтерншип");
            yield return new("offer", "о\u0301ффер");
            yield return new("resume", "резюме\u0301");
            yield return new("CV", "си ви\u0301");
            yield return new("portfolio", "портфо\u0301лио");
            yield return new("interview", "и\u0301нтервью");
            yield return new("onsite", "о\u0301нсайт");
            yield return new("on-site", "о\u0301нсайт");
            yield return new("probation", "пробе\u0301йшн");
            yield return new("salary", "сэ\u0301лэри");
            yield return new("bonus", "бо\u0301нус");
            yield return new("equity", "э\u0301квити");
            yield return new("stock options", "сток о\u0301пшенс");
            yield return new("vesting", "ве\u0301стинг");
            yield return new("layoff", "ле\u0301йофф");
            yield return new("layoffs", "ле\u0301йоффы");
            yield return new("hire", "ха\u0301йр");
            yield return new("hiring", "ха\u0301йринг");
            yield return new("onboarding", "онбо\u0301рдинг");
            yield return new("offboarding", "офбо\u0301рдинг");
            yield return new("performance review", "перфо\u0301рманс ревью\u0301");
            yield return new("feedback", "фи\u0301дбэк");
            yield return new("retro", "ре\u0301тро");
            yield return new("retrospective", "ретроспекти\u0301ва");
            yield return new("roadmap", "ро\u0301удмэп");
            yield return new("scope", "ско\u0301уп");
            yield return new("scope creep", "ско\u0301уп крип");
            yield return new("estimate", "э\u0301стимейт");
            yield return new("estimation", "эстиме\u0301йшн");
            yield return new("story points", "сто\u0301ри по\u0301йнтс");
            yield return new("velocity", "вело\u0301сити");
            yield return new("agile", "э\u0301джайл");
            yield return new("waterfall", "во\u0301терфолл");
            yield return new("jira", "джи\u0301ра");
            yield return new("Jira", "джи\u0301ра");
            yield return new("confluence", "ко\u0301нфлюенс");
            yield return new("Confluence", "ко\u0301нфлюенс");
            yield return new("slack", "слэк");
            yield return new("Slack", "слэк");
            yield return new("teams", "тимз");
            yield return new("Teams", "тимз");
            yield return new("zoom", "зум");
            yield return new("Zoom", "зум");
            yield return new("skype", "скайп");
            yield return new("Skype", "скайп");
            yield return new("webex", "ве\u0301бекс");
            yield return new("WebEx", "ве\u0301бекс");
            yield return new("meet", "мит");
            yield return new("Google Meet", "гу\u0301гл мит");
            yield return new("hangouts", "хэ\u0301нгаутс");
            yield return new("facetime", "фе\u0301йстайм");
            yield return new("FaceTime", "фе\u0301йстайм");
            yield return new("screen share", "скрин шэ\u0301р");
            yield return new("screenshare", "скриншэ\u0301р");
            yield return new("mute", "мьют");
            yield return new("unmute", "анмью\u0301т");
            yield return new("cam", "кам");
            yield return new("camera", "ка\u0301мера");
            yield return new("conference", "ко\u0301нференс");
            yield return new("broadcast", "бро\u0301адкаст");
            yield return new("livestream", "ла\u0301йвстрим");
            yield return new("live stream", "ла\u0301йв стрим");
            yield return new("VOD", "ви оу ди\u0301");
            yield return new("replay", "рипле\u0301й");
            yield return new("highlight", "ха\u0301йлайт");
            yield return new("highlights", "ха\u0301йлайты");
            yield return new("clip", "клип");
            yield return new("clips", "кли\u0301пы");
            yield return new("emote", "имо\u0301ут");
            yield return new("emotes", "имо\u0301уты");
            yield return new("emoji", "эмо\u0301дзи");
            yield return new("sticker", "сти\u0301кер");
            yield return new("stickers", "сти\u0301керы");
            yield return new("gif", "гиф");
            yield return new("reaction", "рие\u0301кшн");
            yield return new("reactions", "рие\u0301кшны");

            // --- frequent Cyrillic spellings of the same anglicisms ---
            yield return new("бэкенд", "бэ\u0301кэнд");
            yield return new("бэкап", "бэ\u0301кап");
            yield return new("фронтенд", "фро\u0301нтэнд");
            yield return new("фулстек", "фу\u0301лстэк");
            yield return new("деплой", "депло\u0301й");
            yield return new("коммит", "коми\u0301т");
            yield return new("мердж", "мёрдж");
            yield return new("ребейз", "рибе\u0301йз");
            yield return new("пулреквест", "пулрекве\u0301ст");
            yield return new("репозиторий", "репозито\u0301рий");
            yield return new("пайплайн", "па\u0301йплайн");
            yield return new("контейнер", "конте\u0301йнер");
            yield return new("докер", "до\u0301кер");
            yield return new("кубернетис", "куберне\u0301тис");
            yield return new("кубер", "ку\u0301бер");
            yield return new("фреймворк", "фре\u0301ймворк");
            yield return new("плагин", "пла\u0301гин");
            yield return new("эндпоинт", "э\u0301ндпоинт");
            yield return new("пейлоад", "пе\u0301йлоад");
            yield return new("хедер", "хе\u0301дер");
            yield return new("куки", "ку\u0301ки");
            yield return new("токен", "то\u0301кен");
            yield return new("логин", "ло\u0301гин");
            yield return new("никнейм", "ни\u0301кнейм");
            yield return new("админ", "а\u0301дмин");
            yield return new("модератор", "модера\u0301тор");
            yield return new("таймаут", "та\u0301ймаут");
            yield return new("дедлайн", "де\u0301длайн");
            yield return new("спринт", "спринт");
            yield return new("бэклог", "бэкло\u0301г");
            yield return new("стендап", "стэ\u0301ндап");
            yield return new("митинг", "ми\u0301тинг");
            yield return new("стрим", "стрим");
            yield return new("стример", "стри\u0301мер");
            yield return new("подкаст", "по\u0301дкаст");
            yield return new("вебинар", "вебина\u0301р");
            yield return new("онлайн", "онла\u0301йн");
            yield return new("офлайн", "офла\u0301йн");
            yield return new("апдейт", "а\u0301пдейт");
            yield return new("апгрейд", "а\u0301пгрейд");
            yield return new("даунлоад", "да\u0301унлоад");
            yield return new("аплоад", "а\u0301плоад");
            yield return new("конфиг", "ко\u0301нфиг");
            yield return new("дефолт", "дефо\u0301лт");
            yield return new("фича", "фи\u0301ча");
            yield return new("фичи", "фи\u0301чи");
            yield return new("баг", "баг");
            yield return new("баги", "баги\u0301");
            yield return new("лаг", "лаг");
            yield return new("пинг", "пинг");
            yield return new("битрейт", "битре\u0301йт");
            yield return new("прокси", "про\u0301кси");
            yield return new("фаервол", "фа\u0301ервол");
            yield return new("файрвол", "фа\u0301ервол");
            yield return new("фишинг", "фи\u0301шинг");
            yield return new("спойлер", "спо\u0301йлер");
            yield return new("скриншот", "скриншо\u0301т");
            yield return new("скрин", "скрин");
            yield return new("браузер", "бра\u0301узер");
            yield return new("хэштег", "хэште\u0301г");
            yield return new("хештег", "хэште\u0301г");
            yield return new("тред", "трэд");
            yield return new("репост", "репо\u0301ст");
            yield return new("лайк", "лайк");
            yield return new("дислайк", "дисла\u0301йк");
            yield return new("фолловер", "фо\u0301лловер");
            yield return new("саб", "саб");
            yield return new("контент", "ко\u0301нтент");
            yield return new("хайп", "хайп");
            yield return new("мем", "мим");
            yield return new("мемы", "ми\u0301мы");
            yield return new("кринж", "кринж");
            yield return new("скилл", "скилл");
            yield return new("нуб", "нуб");
            yield return new("ноуб", "нуб");
            yield return new("геймер", "ге\u0301ймер");
            yield return new("геймплей", "ге\u0301ймплей");
            yield return new("киберспорт", "киберспо\u0301рт");
            yield return new("фраг", "фраг");
            yield return new("хедшот", "хедшо\u0301т");
            yield return new("клатч", "клатч");
            yield return new("респаун", "респа\u0301ун");
            yield return new("лут", "лут");
            yield return new("лутбокс", "лутбо\u0301кс");
            yield return new("скин", "скин");
            yield return new("патч", "патч");
            yield return new("нерф", "нерф");
            yield return new("бафф", "бафф");
            yield return new("баф", "бафф");
            yield return new("мета", "ме\u0301та");
            yield return new("рейд", "рейд");
            yield return new("квест", "квест");
            yield return new("войсчат", "во\u0301йсчат");
            yield return new("копирайт", "ко\u0301пирайт");
            yield return new("опенсорс", "о\u0301упенсорс");
            yield return new("стартап", "ста\u0301ртап");
            yield return new("фрилансер", "фрила\u0301нсер");
            yield return new("фриланс", "фрила\u0301нс");
            yield return new("аутсорсинг", "а\u0301утсорсинг");
            yield return new("менеджер", "ме\u0301неджер");
            yield return new("менеджмент", "ме\u0301неджмент");
            yield return new("маркетинг", "ма\u0301ркетинг");
            yield return new("брендинг", "брэ\u0301ндинг");
            yield return new("бренд", "брэнд");
            yield return new("промо", "промо\u0301");
            yield return new("кэшбэк", "кэшбэ\u0301к");
            yield return new("кешбэк", "кэшбэ\u0301к");
            yield return new("биллинг", "би\u0301ллинг");
            yield return new("маркетплейс", "ма\u0301ркетплейс");
            yield return new("крипто", "кри\u0301пто");
            yield return new("биткоин", "би\u0301ткоин");
            yield return new("блокчейн", "блокче\u0301йн");
            yield return new("майнинг", "ма\u0301йнинг");
            yield return new("майнер", "ма\u0301йнер");
            yield return new("вайтлист", "ва\u0301йтлист");
            yield return new("блеклист", "блэкли\u0301ст");
            yield return new("капча", "ка\u0301пча");
            yield return new("ботнет", "ботне\u0301т");
            yield return new("эксплойт", "экспло\u0301йт");
            yield return new("чейнджлог", "че\u0301йнджлог");
            yield return new("имейл", "и\u0301мейл");
            yield return new("емейл", "и\u0301мейл");
            yield return new("мейл", "мэил");
            yield return new("нотификация", "нотифика\u0301ция");
            yield return new("алерт", "але\u0301рт");
            yield return new("попап", "по\u0301пап");
            yield return new("тултип", "ту\u0301лтип");
            yield return new("сайдбар", "са\u0301йдбар");
            yield return new("лейаут", "ле\u0301йаут");
            yield return new("вайрфрейм", "ва\u0301йрфрейм");
            yield return new("мокап", "мо\u0301кап");
            yield return new("прототип", "прототи\u0301п");
            yield return new("клауд", "кла\u0301уд");
            yield return new("микросервис", "микросе\u0301рвис");
            yield return new("монолит", "мо\u0301нолит");
            yield return new("легаси", "ле\u0301гаси");
            yield return new("рефакторинг", "рефа\u0301кторинг");
            yield return new("кодревью", "кодревью\u0301");
            yield return new("линтер", "ли\u0301нтер");
            yield return new("дебаг", "деба\u0301г");
            yield return new("стэктрейс", "стэктре\u0301йс");
            yield return new("логгинг", "ло\u0301ггинг");
            yield return new("мониторинг", "монито\u0301ринг");
            yield return new("дашборд", "да\u0301шборд");
            yield return new("аналитика", "ана\u0301литика");
            yield return new("лендинг", "лэ\u0301ндинг");
            yield return new("инфлюенсер", "инфлю\u0301енсер");
            yield return new("блогер", "бло\u0301ггер");
            yield return new("влогер", "вло\u0301ггер");
            yield return new("тикток", "тикто\u0301к");
            yield return new("инстаграм", "инстагра\u0301м");
            yield return new("ютуб", "юту\u0301б");
            yield return new("твиттер", "тви\u0301ттер");
            yield return new("фейсбук", "фе\u0301йсбук");
            yield return new("телеграм", "телегра\u0301м");
            yield return new("вотсап", "вотса\u0301п");
            yield return new("ватсап", "вотса\u0301п");
            yield return new("реддит", "ре\u0301ддит");
            yield return new("твич", "твич");
            yield return new("спотифай", "спо\u0301тифай");
            yield return new("нетфликс", "не\u0301тфликс");
            yield return new("гитхаб", "ги\u0301тхаб");
            yield return new("гитлаб", "ги\u0301тлаб");
            yield return new("имиджборд", "и\u0301миджборд");
            yield return new("шитпост", "шитпо\u0301ст");
            yield return new("копипаста", "копипа\u0301ста");
            yield return new("копипаст", "копипе\u0301йст");
            yield return new("троллинг", "тро\u0301ллинг");
            yield return new("флейм", "фле\u0301йм");
            yield return new("оффтопик", "офто\u0301пик");
            yield return new("офтопик", "офто\u0301пик");
            yield return new("триггер", "три\u0301ггер");
            yield return new("косплей", "коспле\u0301й");
            yield return new("аниме", "аниме\u0301");
            yield return new("манга", "ма\u0301нга");
            yield return new("вайб", "вайб");
            yield return new("вайбы", "ва\u0301йбы");
            yield return new("чилл", "чилл");
            yield return new("стресс", "стрэсс");
            yield return new("выгорание", "выгора\u0301ние");
            yield return new("овертайм", "о\u0301вертайм");
            yield return new("удалёнка", "удалёнка");
            yield return new("удаленка", "удалёнка");
            yield return new("коворкинг", "ко\u0301воркинг");
            yield return new("тимбилдинг", "тимби\u0301лдинг");
            yield return new("тимлид", "тимли\u0301д");
            yield return new("техлид", "техли\u0301д");
            yield return new("джуниор", "джу\u0301ниор");
            yield return new("мидл", "ми\u0301ддл");
            yield return new("сеньор", "се\u0301ниор");
            yield return new("синьор", "се\u0301ниор");
            yield return new("оффер", "о\u0301ффер");
            yield return new("резюме", "резюме\u0301");
            yield return new("портфолио", "портфо\u0301лио");
            yield return new("интервью", "и\u0301нтервью");
            yield return new("онбординг", "онбо\u0301рдинг");
            yield return new("фидбек", "фи\u0301дбэк");
            yield return new("фидбэк", "фи\u0301дбэк");
            yield return new("роадмап", "ро\u0301удмэп");
            yield return new("эстимейт", "э\u0301стимейт");
            yield return new("эджайл", "э\u0301джайл");
            yield return new("джира", "джи\u0301ра");
            yield return new("конфлюенс", "ко\u0301нфлюенс");
            yield return new("слэк", "слэк");
            yield return new("скайп", "скайп");
            yield return new("скриншаринг", "скриншэ\u0301ринг");
            yield return new("мьют", "мьют");
            yield return new("анмьют", "анмью\u0301т");
            yield return new("лайвстрим", "ла\u0301йвстрим");
            yield return new("хайлайт", "ха\u0301йлайт");
            yield return new("эмодзи", "эмо\u0301дзи");
            yield return new("эмоджи", "эмо\u0301дзи");
            yield return new("стикер", "сти\u0301кер");
            yield return new("гифка", "ги\u0301фка");
            yield return new("гиф", "гиф");
            yield return new("роллбек", "ро\u0301ллбэк");
            yield return new("хотфикс", "хо\u0301тфикс");
            yield return new("багфикс", "багфи\u0301кс");
            yield return new("чекбокс", "че\u0301кбокс");
            yield return new("дропдаун", "дро\u0301пдаун");
            yield return new("тулбар", "тулба\u0301р");
            yield return new("навбар", "навба\u0301р");
            yield return new("футер", "фу\u0301тер");
            yield return new("юзернейм", "ю\u0301зернейм");
            yield return new("пассворд", "па\u0301ссворд");
            yield return new("сессия", "се\u0301ссия");
            yield return new("кэш", "кэш");
            yield return new("кеш", "кэш");
            yield return new("фаерфокс", "фа\u0301ерфокс");
            yield return new("хром", "хром");
            yield return new("сафари", "сафа\u0301ри");
            yield return new("андроид", "андро\u0301ид");
            yield return new("линукс", "ли\u0301нукс");
            yield return new("убунту", "убу\u0301нту");
            yield return new("винда", "ви\u0301нда");
            yield return new("виндоус", "ви\u0301ндоус");
            yield return new("пайтон", "па\u0301йтон");
            yield return new("джаваскрипт", "джаваскри\u0301пт");
            yield return new("тайпскрипт", "тайпскри\u0301пт");
            yield return new("котлин", "ко\u0301тлин");
            yield return new("реакт", "реа\u0301кт");
            yield return new("ангулар", "а\u0301нгьюлар");
            yield return new("вебсокет", "вебсо\u0301кет");
            yield return new("миддлварь", "ми\u0301ддлвэр");
            yield return new("мидлварь", "ми\u0301ддлвэр");
            yield return new("сервлесс", "сё\u0301рверлес");
            yield return new("серверлесс", "сё\u0301рверлес");
        }
    }
}
