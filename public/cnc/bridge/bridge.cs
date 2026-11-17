using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Threading;

class Mach3Bridge
{
    const string PORT = "8765";

    static dynamic mach = null;
    static dynamic scripter = null;
    static bool isMachConnected = false;
    static readonly object lockObj = new object();

    static bool valveOpen = false;
    static List<int> loweredDispensers = new List<int>();
    static int activeDispenserId = 1;
    static int totalLines = 0;
    static string loadedGcodeHash = "";
    static string loadedFileName = "";
    static int gcodeFileSlot = 0;
    static string valveOpenCmd = "M11 P3";
    static string valveCloseCmd = "M10 P3";
    static Dictionary<int, string> dispenserLowerCmds = new Dictionary<int, string>();
    static Dictionary<int, string> dispenserRaiseCmds = new Dictionary<int, string>();

    static void InitDefaultCmds()
    {
        valveOpenCmd = "M11 P3";
        valveCloseCmd = "M10 P3";
        for (int i = 1; i <= 5; i++)
        {
            dispenserLowerCmds[i] = "M11 P" + (3 + i);
            dispenserRaiseCmds[i] = "M10 P" + (3 + i);
        }
    }

    static bool EnsureMach3()
    {
        lock (lockObj)
        {
            if (Process.GetProcessesByName("Mach3").Length == 0)
            {
                if (isMachConnected)
                {
                    LogWarning("[MACH3] Mach3 е исклучен.");
                    isMachConnected = false;
                    mach = null;
                    scripter = null;
                    loadedGcodeHash = "";
                    loadedFileName = "";
                }
                return false;
            }

            if (isMachConnected && scripter != null) return true;

            try
            {
                object activeObj = Marshal.GetActiveObject("Mach4.Document");
                mach = activeObj;
                scripter = mach.GetScriptDispatch();

                // Проверка само на X оска
                double x = Convert.ToDouble(scripter.GetDRO(0));

                isMachConnected = true;
                LogSuccess("[MACH3] УСПЕШНО ПОВРЗАН СО ВАШИОТ DRIVER-MACH3.JS!");
                return true;
            }
            catch
            {
                isMachConnected = false;
                mach = null;
                scripter = null;
                return false;
            }
        }
    }

    static string UnescapeJsonString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        StringBuilder sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                char next = raw[i + 1];
                if (next == 'n') { sb.Append('\n'); i++; }
                else if (next == 'r') { sb.Append('\r'); i++; }
                else if (next == 't') { sb.Append('\t'); i++; }
                else if (next == 'b') { sb.Append('\b'); i++; }
                else if (next == 'f') { sb.Append('\f'); i++; }
                else if (next == '"') { sb.Append('"'); i++; }
                else if (next == '\\') { sb.Append('\\'); i++; }
                else if (next == '/') { sb.Append('/'); i++; }
                else if (next == 'u' && i + 5 < raw.Length)
                {
                    string hex = raw.Substring(i + 2, 4);
                    int code;
                    if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    {
                        sb.Append((char)code);
                        i += 5;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    sb.Append(next);
                    i++;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    static string ExtractJsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
        Match mKey = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"");
        if (!mKey.Success) return null;

        int pos = mKey.Index + mKey.Length;
        StringBuilder raw = new StringBuilder();
        while (pos < json.Length)
        {
            char c = json[pos];
            if (c == '\\' && pos + 1 < json.Length)
            {
                raw.Append(c);
                raw.Append(json[pos + 1]);
                pos += 2;
                continue;
            }
            if (c == '"') break;
            raw.Append(c);
            pos++;
        }
        return UnescapeJsonString(raw.ToString());
    }

    static double ExtractJsonDouble(string json, string key, double fallback)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return fallback;
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*([-0-9.]+)");
        if (!m.Success) return fallback;
        double val;
        if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
        {
            return val;
        }
        return fallback;
    }

    static void ApplyConfigFromBody(string body)
    {
        if (string.IsNullOrEmpty(body)) return;

        string vOpen = ExtractJsonString(body, "valveOpenCmd");
        if (!string.IsNullOrEmpty(vOpen)) valveOpenCmd = vOpen.ToUpper().Trim();

        string vClose = ExtractJsonString(body, "valveCloseCmd");
        if (!string.IsNullOrEmpty(vClose)) valveCloseCmd = vClose.ToUpper().Trim();

        Match mArr = Regex.Match(body, "\"dispensers\"\\s*:\\s*\\[([\\s\\S]*?)\\]");
        if (mArr.Success)
        {
            string arrContent = mArr.Groups[1].Value;
            MatchCollection objs = Regex.Matches(arrContent, "\\{([^}]+)\\}");
            foreach (Match objMatch in objs)
            {
                string objJson = objMatch.Groups[1].Value;
                Match mId = Regex.Match(objJson, "\"id\"\\s*:\\s*(\\d+)");
                if (!mId.Success) continue;
                int dId = int.Parse(mId.Groups[1].Value, CultureInfo.InvariantCulture);

                string lCmd = ExtractJsonString(objJson, "lowerCmd");
                string rCmd = ExtractJsonString(objJson, "raiseCmd");
                if (!string.IsNullOrEmpty(lCmd)) dispenserLowerCmds[dId] = lCmd.ToUpper().Trim();
                if (!string.IsNullOrEmpty(rCmd)) dispenserRaiseCmds[dId] = rCmd.ToUpper().Trim();
            }
        }
    }

    static bool MatchesCommandLine(string lineUpper, string cmdUpper)
    {
        if (string.IsNullOrEmpty(lineUpper) || string.IsNullOrEmpty(cmdUpper)) return false;
        return lineUpper == cmdUpper ||
               lineUpper.StartsWith(cmdUpper + " ") ||
               lineUpper.StartsWith(cmdUpper + "\t") ||
               lineUpper.StartsWith(cmdUpper + ";");
    }

    static void TrackCommandLineState(string lineUpper)
    {
        if (string.IsNullOrEmpty(lineUpper)) return;
        int commentIdx = lineUpper.IndexOf(';');
        string clean = (commentIdx >= 0 ? lineUpper.Substring(0, commentIdx) : lineUpper).Trim();
        if (string.IsNullOrEmpty(clean)) return;

        if (MatchesCommandLine(clean, valveOpenCmd))
        {
            valveOpen = true;
            return;
        }
        if (MatchesCommandLine(clean, valveCloseCmd))
        {
            valveOpen = false;
            return;
        }
        foreach (KeyValuePair<int, string> kvp in dispenserLowerCmds)
        {
            if (MatchesCommandLine(clean, kvp.Value))
            {
                if (!loweredDispensers.Contains(kvp.Key)) loweredDispensers.Add(kvp.Key);
                activeDispenserId = kvp.Key;
                return;
            }
        }
        foreach (KeyValuePair<int, string> kvp in dispenserRaiseCmds)
        {
            if (MatchesCommandLine(clean, kvp.Value))
            {
                loweredDispensers.Remove(kvp.Key);
                return;
            }
        }
    }

    static string EscapeJsonStr(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
    }

    // 100% БЕЗБЕДНО КРЕИРАЊЕ НА SNAPSHOT
    static string BuildSnapshotJson(string errorMsg = null, bool loadedFlag = false)
    {
        lock (lockObj)
        {
            double x = 0, y = 0, feed = 0;
            int lineNum = 0;
            bool isMoving = false;
            bool isReset = false;
            bool isCycleStart = false;
            bool isHold = false;
            string status = "DISCONNECTED";

            if (isMachConnected && scripter != null)
            {
                try { x = Convert.ToDouble(scripter.GetDRO(0)); } catch { }
                try { y = Convert.ToDouble(scripter.GetDRO(1)); } catch { }
                try { feed = Convert.ToDouble(scripter.GetOEMDRO(818)); } catch { }
                try { lineNum = Convert.ToInt32(scripter.GetOEMDRO(816)); } catch { }

                try { isMoving = Convert.ToInt32(scripter.IsMoving()) != 0; } catch { }
                try { isReset = Convert.ToInt32(scripter.GetOEMLED(800)) != 0; } catch { }
                try { isCycleStart = Convert.ToInt32(scripter.GetOEMLED(804)) != 0; } catch { }
                try { isHold = Convert.ToInt32(scripter.GetOEMLED(805)) != 0; } catch { }

                if (isReset) status = "ESTOP";
                else if (isHold) status = "HOLD";
                else if (isMoving || isCycleStart) status = "RUNNING";
                else status = "IDLE";

                // Проверка дали во Mach3 е вчитан друг фајл рачно од корисникот
                if (!string.IsNullOrEmpty(loadedFileName))
                {
                    try
                    {
                        string curMachFile = Convert.ToString(mach.FileName);
                        if (!string.IsNullOrEmpty(curMachFile) &&
                            !string.Equals(Path.GetFileName(curMachFile), Path.GetFileName(loadedFileName), StringComparison.OrdinalIgnoreCase))
                        {
                            loadedGcodeHash = "";
                        }
                    }
                    catch { }
                }
            }

            loweredDispensers.Sort();
            string loweredJson = "[" + string.Join(",", loweredDispensers.ToArray()) + "]";
            string errStr = string.IsNullOrEmpty(errorMsg) ? "null" : "\"" + EscapeJsonStr(errorMsg) + "\"";
            string hashJson = "\"" + EscapeJsonStr(loadedGcodeHash) + "\"";
            string fileJson = "\"" + EscapeJsonStr(loadedFileName) + "\"";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"status\":\"{0}\",\"mach3Connected\":{1},\"x\":{2:F3},\"y\":{3:F3},\"feedrate\":{4:F0},\"lineNum\":{5},\"totalLines\":{6},\"valveOpen\":{7},\"loweredDispensers\":{8},\"activeDispenserId\":{9},\"loaded\":{10},\"loadedGcodeHash\":{11},\"loadedFile\":{12},\"error\":{13}}}",
                status,
                isMachConnected ? "true" : "false",
                x, y, feed, lineNum, totalLines,
                valveOpen ? "true" : "false",
                loweredJson,
                activeDispenserId,
                loadedFlag ? "true" : "false",
                hashJson,
                fileJson,
                errStr
            );
        }
    }

    static string SaveGcodeToSafeTempFile(string normalizedGcode, out string errMsg)
    {
        errMsg = null;
        string tempDir = Path.GetTempPath();

        // 10 ротирачки слотови: job_1.tap .. job_10.tap (8.3 формат, компатибилен со сите верзии на Mach3 и Windows).
        // Доколку Mach3 моментално држи отворена претходна датотека (на пример job_1.tap),
        // при обид за запишување се јавува IOException. Ние го фаќаме овој исклучок и автоматски
        // преминуваме на следниот слободен слот, спречувајќи заклучување и за „Пушти активен таб“ и за „Почни од тука“.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int slot = Interlocked.Increment(ref gcodeFileSlot);
            if (slot > 1000000) gcodeFileSlot = 1;
            int slotNum = ((slot - 1) % 10) + 1;
            string candidate = Path.Combine(tempDir, string.Format(CultureInfo.InvariantCulture, "job_{0}.tap", slotNum));

            try
            {
                using (var fs = new FileStream(candidate, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    writer.Write(normalizedGcode);
                    writer.Flush();
                }
                return candidate;
            }
            catch (IOException ioEx)
            {
                LogWarning("[FILE] Датотеката " + Path.GetFileName(candidate) + " е моментално зафатена од Mach3, префрлање на следен слот (" + ioEx.Message + ")");
                continue;
            }
            catch (Exception ex)
            {
                errMsg = "Грешка при запишување на привремена датотека " + Path.GetFileName(candidate) + ": " + ex.Message;
                LogError("[FILE ГРЕШКА] " + errMsg);
                return null;
            }
        }

        // Резервна варијанта: уникатно име со датум/време
        try
        {
            string guidFile = Path.Combine(tempDir, string.Format(CultureInfo.InvariantCulture, "job_{0}_{1}.tap",
                DateTime.Now.ToString("HHmmss"),
                Guid.NewGuid().ToString("N").Substring(0, 4)));

            using (var fs = new FileStream(guidFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.Write(normalizedGcode);
                writer.Flush();
            }
            return guidFile;
        }
        catch (Exception ex)
        {
            errMsg = "Сите слотови за привремена датотека беа блокирани од системот: " + ex.Message;
            LogError("[FILE ГРЕШКА] " + errMsg);
            return null;
        }
    }

    static bool LoadGcodeIntoMach3Locked(string gcode, string gcodeHash, out string errMsg)
    {
        errMsg = null;
        if (!isMachConnected || scripter == null)
        {
            errMsg = "Mach3 не е поврзан.";
            return false;
        }
        if (string.IsNullOrEmpty(gcode))
        {
            errMsg = "Испразниот G-код не може да се вчита во Mach3.";
            return false;
        }

        try
        {
            string normalized = gcode.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            string tempFile = SaveGcodeToSafeTempFile(normalized, out errMsg);
            if (string.IsNullOrEmpty(tempFile) || !File.Exists(tempFile))
            {
                if (string.IsNullOrEmpty(errMsg)) errMsg = "Неуспешно креирање на безбедна привремена датотека за G-код.";
                return false;
            }

            totalLines = normalized.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            loadedGcodeHash = "";
            loadedFileName = tempFile;

            scripter.LoadFile(tempFile);

            // Кратка иницијална пауза за Mach3 да ја стартува нишката за вчитување пред IsLoading() проверката
            Thread.Sleep(180);

            int maxWaitMs = 15000;
            int waited = 0;
            while (Convert.ToInt32(scripter.IsLoading()) != 0 && waited < maxWaitMs)
            {
                Thread.Sleep(50);
                waited += 50;
            }

            if (Convert.ToInt32(scripter.IsLoading()) != 0)
            {
                errMsg = "Истече времето при вчитување на G-кодот во Mach3.";
                return false;
            }

            Thread.Sleep(120);

            try
            {
                string machFile = Convert.ToString(mach.FileName);
                string expectedBaseName = Path.GetFileName(tempFile);
                if (!string.IsNullOrEmpty(machFile) &&
                    machFile.IndexOf(expectedBaseName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    LogWarning("[LOAD] Mach3 FileName врати: " + machFile + " (очекувано: " + expectedBaseName + ")");
                }
            }
            catch { }

            loadedGcodeHash = string.IsNullOrEmpty(gcodeHash) ? ("L" + totalLines) : gcodeHash;
            LogSuccess("[LOAD] G-кодот е успешно вчитан во Mach3 (" + totalLines + " реда, датотека: " + Path.GetFileName(tempFile) + ").");
            return true;
        }
        catch (Exception ex)
        {
            errMsg = "Грешка при вчитување во Mach3: " + ex.Message;
            LogError("[LOAD ГРЕШКА] " + ex.Message);
            return false;
        }
    }

    static bool StartLoadedInMach3Locked(out string errMsg)
    {
        errMsg = null;
        if (!isMachConnected || scripter == null)
        {
            errMsg = "Mach3 не е поврзан.";
            return false;
        }

        try
        {
            bool isReset = false;
            try { isReset = Convert.ToInt32(scripter.GetOEMLED(800)) != 0; } catch { }
            if (isReset)
            {
                errMsg = "Mach3 е во Reset / E-STOP состојба! Прво притиснете Reset во Mach3 или 🔄 Reset во апликацијата.";
                return false;
            }

            if (totalLines <= 0 || string.IsNullOrEmpty(loadedFileName))
            {
                errMsg = "Во Mach3 нема вчитан G-код од апликацијата.";
                return false;
            }

            bool isHold = false;
            try { isHold = Convert.ToInt32(scripter.GetOEMLED(805)) != 0; } catch { }
            if (!isHold)
            {
                scripter.DoOEMButton(1002); // Rewind на ред 0 пред старт
                Thread.Sleep(100);
            }

            scripter.DoOEMButton(1000); // Cycle Start
            Thread.Sleep(120);
            LogSuccess("[RUN] Стартувано извршување во Mach3 (Cycle Start).");
            return true;
        }
        catch (Exception ex)
        {
            errMsg = "Грешка при стартување на Mach3: " + ex.Message;
            LogError("[RUN ГРЕШКА] " + ex.Message);
            return false;
        }
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Mach3 Driver Bridge V2.1.4 (Port: " + PORT + ")";
        InitDefaultCmds();

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogError("\n[ГРЕШКА]: " + e.ExceptionObject.ToString());
            Console.ReadLine();
        };

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("===============================================================");
        Console.WriteLine("       MACH3 ПОСВЕТЕН МОСТ ЗА CNC УРЕДУВАЧОТ (V2.1.4)          ");
        Console.WriteLine("===============================================================");
        Console.ResetColor();

        EnsureMach3();

        HttpListener listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:" + PORT + "/");
        listener.Prefixes.Add("http://localhost:" + PORT + "/");

        try
        {
            listener.Start();
            LogSuccess("[МРЕЖА] Бриџот слуша на: http://127.0.0.1:" + PORT + "/");
            Console.WriteLine("---------------------------------------------------------------\n");
        }
        catch (Exception ex)
        {
            LogError("[ГРЕШКА] Портата е зафатена: " + ex.Message);
            Console.ReadLine();
            return;
        }

        while (true)
        {
            try
            {
                HttpListenerContext context = listener.GetContext();
                ProcessRequest(context);
            }
            catch { }
        }
    }

    static void ProcessRequest(HttpListenerContext context)
    {
        HttpListenerRequest req = context.Request;
        HttpListenerResponse res = context.Response;

        try
        {
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
            res.AddHeader("Access-Control-Allow-Headers", "Content-Type, Origin, Accept");
            res.AddHeader("Access-Control-Allow-Private-Network", "true");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 200;
                res.Close();
                return;
            }

            string rawUrl = req.Url.AbsolutePath.ToLower();
            string time = DateTime.Now.ToString("HH:mm:ss");

            EnsureMach3();

            string body = "";
            if (req.HasEntityBody)
            {
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                {
                    body = reader.ReadToEnd();
                }
            }

            // 1. GET /status
            if (rawUrl.EndsWith("/status") && req.HttpMethod == "GET")
            {
                SendJson(res, BuildSnapshotJson());
                return;
            }

            // 2. POST /config
            if (rawUrl.EndsWith("/config") && req.HttpMethod == "POST")
            {
                Console.WriteLine("[" + time + "] [CONFIG] Синхронизација со апликацијата.");
                lock (lockObj)
                {
                    ApplyConfigFromBody(body);
                }
                SendJson(res, BuildSnapshotJson());
                return;
            }

            // 3A. POST /load_gcode (само вчитување и потврда од Mach3)
            if (rawUrl.EndsWith("/load_gcode") && req.HttpMethod == "POST")
            {
                Console.WriteLine("[" + time + "] [LOAD] Вчитување на G-код во Mach3...");
                string gcode = ExtractJsonString(body, "gcode") ?? "";
                string gcodeHash = ExtractJsonString(body, "gcodeHash") ?? "";

                string loadErr = null;
                bool ok = false;
                lock (lockObj)
                {
                    ApplyConfigFromBody(body);
                    ok = LoadGcodeIntoMach3Locked(gcode, gcodeHash, out loadErr);
                }
                if (!ok)
                {
                    SendJson(res, BuildSnapshotJson(loadErr, false), 400);
                    return;
                }
                SendJson(res, BuildSnapshotJson(null, true));
                return;
            }

            // 3B. POST /run_loaded (старт на веќе вчитан G-код без повторно праќање)
            if (rawUrl.EndsWith("/run_loaded") && req.HttpMethod == "POST")
            {
                Console.WriteLine("[" + time + "] [RUN_LOADED] Старт на веќе вчитаниот G-код.");
                string runErr = null;
                bool ok = false;
                lock (lockObj)
                {
                    ok = StartLoadedInMach3Locked(out runErr);
                }
                if (!ok)
                {
                    SendJson(res, BuildSnapshotJson(runErr, !string.IsNullOrEmpty(loadedGcodeHash)), 400);
                    return;
                }
                SendJson(res, BuildSnapshotJson(null, true));
                return;
            }

            // 3C. POST /load_and_run (комбинирано за компатибилност)
            if (rawUrl.EndsWith("/load_and_run") && req.HttpMethod == "POST")
            {
                Console.WriteLine("[" + time + "] [LOAD_AND_RUN] Вчитување и старт на G-код!");
                string gcode = ExtractJsonString(body, "gcode") ?? "";
                string gcodeHash = ExtractJsonString(body, "gcodeHash") ?? "";

                string errMsg = null;
                bool ok = false;
                lock (lockObj)
                {
                    ApplyConfigFromBody(body);
                    ok = LoadGcodeIntoMach3Locked(gcode, gcodeHash, out errMsg);
                    if (ok)
                    {
                        ok = StartLoadedInMach3Locked(out errMsg);
                    }
                }
                if (!ok)
                {
                    SendJson(res, BuildSnapshotJson(errMsg, false), 400);
                    return;
                }
                SendJson(res, BuildSnapshotJson(null, true));
                return;
            }

            // 4. КОНТРОЛИ: /pause, /resume, /stop, /reset
            if (rawUrl.EndsWith("/pause") && req.HttpMethod == "POST")
            {
                Console.WriteLine("[" + time + "] [ACTION] PAUSE");
                lock (lockObj) { if (isMachConnected && scripter != null) scripter.DoOEMButton(1001); }
                SendJson(res, BuildSnapshotJson());
                return;
            }
            if (rawUrl.EndsWith("/resume") && req.HttpMethod == "POST")
            {
                Console.WriteLine("[" + time + "] [ACTION] RESUME");
                lock (lockObj) { if (isMachConnected && scripter != null) scripter.DoOEMButton(1000); }
                SendJson(res, BuildSnapshotJson());
                return;
            }
            if (rawUrl.EndsWith("/stop") && req.HttpMethod == "POST")
            {
                Console.WriteLine("[" + time + "] [ACTION] STOP");
                lock (lockObj)
                {
                    if (isMachConnected && scripter != null) scripter.DoOEMButton(1003);
                    valveOpen = false;
                    loweredDispensers.Clear();
                }
                SendJson(res, BuildSnapshotJson());
                return;
            }
            if (rawUrl.EndsWith("/reset") && req.HttpMethod == "POST")
            {
                Console.WriteLine("[" + time + "] [ACTION] RESET");
                lock (lockObj)
                {
                    if (isMachConnected && scripter != null) scripter.DoOEMButton(1021);
                    valveOpen = false;
                    loweredDispensers.Clear();
                }
                SendJson(res, BuildSnapshotJson());
                return;
            }

            // 5. POST /jog (релативно од тековното DRO без G91/G90 конфликт во ист ред)
            if (rawUrl.EndsWith("/jog") && req.HttpMethod == "POST")
            {
                string axis = (ExtractJsonString(body, "axis") ?? "").ToUpper().Trim();
                double step = ExtractJsonDouble(body, "step", 0.0);
                if ((axis == "X" || axis == "Y") && Math.Abs(step) > 0.00001)
                {
                    lock (lockObj)
                    {
                        if (isMachConnected && scripter != null)
                        {
                            double cur = 0;
                            try { cur = Convert.ToDouble(scripter.GetDRO(axis == "X" ? 0 : 1)); } catch { }
                            double target = Math.Round(cur + step, 3);
                            string jogGCode = string.Format(CultureInfo.InvariantCulture, "G90 G0 {0}{1:F3}", axis, target);
                            Console.WriteLine("[" + time + "] [JOG] (DRO=" + cur.ToString("F3", CultureInfo.InvariantCulture) + ", step=" + step.ToString("F3", CultureInfo.InvariantCulture) + ") -> " + jogGCode);
                            scripter.Code(jogGCode);
                        }
                    }
                }
                SendJson(res, BuildSnapshotJson());
                return;
            }

            // 6. POST /zero
            if (rawUrl.EndsWith("/zero") && req.HttpMethod == "POST")
            {
                string axis = (ExtractJsonString(body, "axis") ?? "").ToUpper().Trim();
                if (!string.IsNullOrEmpty(axis))
                {
                    Console.WriteLine("[" + time + "] [ZERO] Нулирање на оска " + axis);
                    lock (lockObj)
                    {
                        if (isMachConnected && scripter != null)
                        {
                            if (axis == "X") scripter.DoOEMButton(1007);
                            else if (axis == "Y") scripter.DoOEMButton(1008);
                            else if (axis == "XY" || axis == "ALL")
                            {
                                scripter.DoOEMButton(1007);
                                scripter.DoOEMButton(1008);
                            }
                            else if (axis == "Z") scripter.DoOEMButton(1009);
                        }
                    }
                }
                SendJson(res, BuildSnapshotJson());
                return;
            }

            // 7. POST /mdi (со правилно JSON декодирање на \n и поддршка за повеќе редови + микро-чекори)
            if (rawUrl.EndsWith("/mdi") && req.HttpMethod == "POST")
            {
                string cmd = ExtractJsonString(body, "cmd");
                if (cmd != null)
                {
                    Console.WriteLine("[" + time + "] [MDI] " + cmd.Replace("\r", "").Replace("\n", " | "));

                    lock (lockObj)
                    {
                        double baseX = 0;
                        if (isMachConnected && scripter != null)
                        {
                            try { baseX = Convert.ToDouble(scripter.GetDRO(0)); } catch { }
                        }

                        if (cmd == "RAISE_ALL")
                        {
                            double feedVal = ExtractJsonDouble(body, "feed", 3000);
                            int feed = Math.Max(1, (int)Math.Round(feedVal));
                            MatchCollection mRaises = Regex.Matches(body, "\"raiseCmds\"\\s*:\\s*\\[([^\\]]+)\\]");
                            if (mRaises.Count > 0)
                            {
                                string inside = mRaises[0].Groups[1].Value;
                                int stepDir = 1;
                                foreach (Match sub in Regex.Matches(inside, "\"([^\"]+)\""))
                                {
                                    if (isMachConnected && scripter != null)
                                    {
                                        string rCmd = UnescapeJsonString(sub.Groups[1].Value).Trim();
                                        if (string.IsNullOrEmpty(rCmd)) continue;
                                        double targetX = (stepDir == 1) ? (baseX + 0.001) : baseX;
                                        stepDir = -stepDir;
                                        scripter.Code(rCmd);
                                        scripter.Code(string.Format(CultureInfo.InvariantCulture, "G1 X{0:F3} F{1}", targetX, feed));
                                    }
                                }
                            }
                            loweredDispensers.Clear();
                        }
                        else if (cmd.StartsWith("PULSE:"))
                        {
                            int duration = 100;
                            int.TryParse(cmd.Substring(6), out duration);
                            if (duration <= 0) duration = 100;
                            if (duration > 5000) duration = 5000;

                            string pOpen = ExtractJsonString(body, "openCmd");
                            string pClose = ExtractJsonString(body, "closeCmd");
                            if (string.IsNullOrEmpty(pOpen)) pOpen = valveOpenCmd;
                            if (string.IsNullOrEmpty(pClose)) pClose = valveCloseCmd;
                            double feedVal = ExtractJsonDouble(body, "feed", 3000);
                            int feed = Math.Max(1, (int)Math.Round(feedVal));

                            if (isMachConnected && scripter != null)
                            {
                                scripter.Code(pOpen);
                                scripter.Code(string.Format(CultureInfo.InvariantCulture, "G1 X{0:F3} F{1}", baseX + 0.001, feed));
                                System.Threading.Thread.Sleep(duration + 40);
                                scripter.Code(pClose);
                                scripter.Code(string.Format(CultureInfo.InvariantCulture, "G1 X{0:F3} F{1}", baseX, feed));
                            }
                            valveOpen = false;
                        }
                        else
                        {
                            string[] lines = cmd.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (string line in lines)
                            {
                                string trimmed = line.Trim();
                                if (string.IsNullOrEmpty(trimmed)) continue;

                                string upperLine = trimmed.ToUpper();
                                if (isMachConnected && scripter != null)
                                {
                                    scripter.Code(trimmed);
                                    if (upperLine.StartsWith("G4 ") || upperLine.StartsWith("G4P"))
                                    {
                                        Match mp = Regex.Match(upperLine, "P\\s*([0-9]+(?:\\.[0-9]+)?)");
                                        if (mp.Success)
                                        {
                                            double pMs;
                                            if (double.TryParse(mp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pMs))
                                            {
                                                int sleepMs = (int)Math.Round(pMs);
                                                if (sleepMs > 0 && sleepMs <= 5000)
                                                {
                                                    System.Threading.Thread.Sleep(sleepMs);
                                                }
                                            }
                                        }
                                    }
                                }
                                TrackCommandLineState(upperLine);
                            }
                        }
                    }
                }
                SendJson(res, BuildSnapshotJson());
                return;
            }

            // 8. Сервисирање статички датотеки (index.html, js, css)
            string filename = rawUrl == "/" ? "index.html" : rawUrl.TrimStart('/');
            string localFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);

            if (File.Exists(localFilePath))
            {
                byte[] fileBytes = File.ReadAllBytes(localFilePath);
                string contentType = "application/octet-stream";
                if (localFilePath.EndsWith(".html")) contentType = "text/html; charset=utf-8";
                else if (localFilePath.EndsWith(".js")) contentType = "application/javascript";
                else if (localFilePath.EndsWith(".css")) contentType = "text/css";

                res.ContentType = contentType;
                res.ContentLength64 = fileBytes.Length;
                res.OutputStream.Write(fileBytes, 0, fileBytes.Length);
                res.OutputStream.Close();
                return;
            }

            SendJson(res, BuildSnapshotJson());
        }
        catch (Exception ex)
        {
            LogError("[HTTP ГРЕШКА] " + ex.Message);
            try
            {
                SendJson(res, BuildSnapshotJson(ex.Message, false), 500);
            }
            catch
            {
                try { res.Close(); } catch { }
            }
        }
    }

    static void SendJson(HttpListenerResponse res, string json, int statusCode = 200)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(json);
        res.StatusCode = statusCode;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = buffer.Length;
        res.OutputStream.Write(buffer, 0, buffer.Length);
        res.OutputStream.Close();
    }

    static void LogSuccess(string msg) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(msg); Console.ResetColor(); }
    static void LogWarning(string msg) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(msg); Console.ResetColor(); }
    static void LogError(string msg) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(msg); Console.ResetColor(); }
}