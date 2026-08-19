using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using EasyFM350.Wpf.Backend.Config;
using EasyFM350.Wpf.Backend.Esim;
using EasyFM350.Wpf.Backend.Infrastructure;
using EasyFM350.Wpf.Backend.Modem;
using EasyFM350.Wpf.Backend.Network;
using EasyFM350.Wpf.Backend.Radio;

namespace EasyFM350.Tests
{

    public class FakeTransport : ITransport
    {
        private readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly StringBuilder _out = new StringBuilder();
        public readonly List<string> Sent = new List<string>();
        public bool IsOpen { get; private set; }

        public bool Chunked;

        public int ChunkAt = -1;

        public bool Silent;

        public string InjectNext;

        public string StaleBeforeNext;

        public bool FailOnWrite;

        public int FailNextWrites;

        public bool NoOk;

        private readonly System.Collections.Generic.Queue<string> _chunks = new System.Collections.Generic.Queue<string>();

        public FakeTransport On(string cmd, string responseBody)
        {
            _map[cmd] = responseBody;
            return this;
        }

        public void Open() { IsOpen = true; }
        public void Close() { IsOpen = false; }

        public void Write(string s)
        {
            var cmd = s.TrimEnd('\r');
            Sent.Add(cmd);
            if (FailOnWrite) throw new System.IO.IOException("cable yanked");
            if (Silent) return;
            if (FailNextWrites > 0)
            {
                FailNextWrites--;
                _out.Append(cmd).Append('\r').Append("\r\nERROR\r\n");
                return;
            }
            string body;
            if (!_map.TryGetValue(cmd, out body))
            {
                _out.Append(cmd).Append('\r').Append("\r\nERROR\r\n");
                return;
            }
            var full = cmd + "\r" + body + (NoOk ? "" : "\r\nOK\r\n");
            if (InjectNext != null) { full = full.Insert(full.IndexOf("\r\nOK"), InjectNext); InjectNext = null; }
            if (StaleBeforeNext != null)
            {
                _chunks.Enqueue(StaleBeforeNext);
                _chunks.Enqueue(full);
                StaleBeforeNext = null;
                return;
            }
            if (Chunked)
            {
                var cut = ChunkAt >= 0 && ChunkAt <= full.Length ? ChunkAt : full.Length / 2;
                _chunks.Enqueue(full.Substring(0, cut));
                _chunks.Enqueue(full.Substring(cut));
            }
            else _out.Append(full);
        }

        public string ReadAvailable()
        {
            if (_chunks.Count > 0) return _chunks.Dequeue();
            var s = _out.ToString();
            _out.Length = 0;
            return s;
        }

        public static FakeTransport StandardFm350()
        {
            return new FakeTransport()
                .On("AT", "")
                .On("AT+CPIN?", "\r\n+CPIN: READY\r\n")
                .On("AT+COPS=0", "")
                .On("AT+CIMI", "\r\n250015893949774\r\n")
                .On("AT+CGSN", "\r\n352455106006257\r\n")
                .On("ATI", "\r\nManufacturer: Fibocom Wireless Inc.\r\nModel: FM350-GL\r\nRevision: 81600.0000.00.29.24.02\r\n")
                .On("AT+COPS?", "\r\n+COPS:0,2,\"25001\",7\r\n")
                .On("AT+CGDCONT=1,\"IP\",\"internet.mts.ru\"", "")
                .On("AT+CGACT=1,1", "\r\n+CGEV: ME PDN ACT 1\r\n")
                .On("AT+CGPADDR=1; +GTDNS=1", "\r\n+CGPADDR: 1,\"11.24.224.123\",\"0.0.0.0.0.0.0.0.0.0.0.0.108.65.63.134.1\"\r\n\r\n+GTDNS: 1,\"213.87.142.95\",\"213.87.142.94\"\r\n")
                .On("AT+CSQ", "\r\n+CSQ: 10, 99\r\n")
                .On("AT+RSRP?", "\r\n+RSRP: 116, 3250, -93\r\n")
                .On("AT+GTCCINFO?", "\r\n+GTCCINFO: \r\n1,4,250,1,91DC,00111EE03,3250,116,107,100,14,47,47,22\r\n")
                .On("AT+GTCAINFO?", "\r\n+GTCAINFO: PCC:107,116,3250,100,100,1,1,1,3,-93 SCC 1:2,0,101,491,400,50,255,0,255,0,255,-93 SCC 2:2,0,103,470,1725,75,255,0,255,0,255,-93\r\n")
                .On("AT+GTACT?", "\r\n+GTACT: 20,6,3,1,2,4,5,8,101,102,103,104,105,107,108,112,113,114,117,118,119,120,125,126,128,129,130,132,134,138,139,140,141,142,143,146,148,166,171,501,502,503,505,507,508,5020,5025,5028,5030,5038,5040,5041,5048,5066,5071,5077,5078,5079\r\n")
                .On("AT+GTRFHWVER?", "\r\n+GTRFHWVER: \"V1.0.1\"\r\n")
                .On("AT+ECAL?", "\r\n+ECAL: 1\r\n")
                .On("AT+GTQUERYCALI?", "\r\n+GTQUERYCALI: 0\r\n");
        }
    }

    public static class Tests
    {
        private static int _pass, _fail;

        private static void Ok(bool cond, string name)
        {
            if (cond) { _pass++; Console.WriteLine("PASS  " + name); }
            else { _fail++; Console.WriteLine("FAIL  " + name); }
        }

        private static void Eq(string a, string b, string name)
        {
            Ok(a == b, name + (a == b ? "" : "  [expected '" + b + "' got '" + a + "']"));
        }

        private static void Throws<T>(Action action, string name) where T : Exception
        {
            try
            {
                action();
                Ok(false, name);
            }
            catch (T)
            {
                Ok(true, name);
            }
        }

        public static int Run()
        {
            Console.WriteLine("== unit: Modem parsers ==");
            Eq(Modem.Number("AT+CIMI\r\r\n250015893949774\r\n\r\nOK\r\n"), "250015893949774", "Number() IMSI");
            Eq(Modem.Number("AT+CGSN\r\r\n352455106006257\r\n\r\nOK\r\n"), "352455106006257", "Number() IMEI");
            var f = Modem.Fields("+GTCCINFO: \r\n1,4,250,1,91DC,00111EE03,3250,116,107,100,14,47,47,22\r\nOK", "+GTCCINFO");
            Ok(f.Length == 14 && f[6] == "3250" && f[7] == "116", "Fields() GTCCINFO");
            Ok(Modem.Fields("+GTSENRDTEMP:\r\n\r\nOK", "+GTSENRDTEMP").Length == 0, "Fields() empty value → no OK-as-data");

            Console.WriteLine("== unit: PdpContext ==");
            var contrdpNoMask = "\r\n+CGCONTRDP: 1,5,\"\",\"11.210.132.30\",\"11.210.132.29\",\"213.87.142.95\",\"213.87.142.94\"\r\nOK";
            Ok(PdpContext.TryParseIpv4(contrdpNoMask, 1, out var pdpNoMask), "CGCONTRDP 4-octet local addr parsed");
            Eq(pdpNoMask.SubnetMask, "255.255.255.255", "CGCONTRDP missing mask → /32 fallback");
            Eq(pdpNoMask.LocalAddress, "11.210.132.30", "CGCONTRDP no-mask address");
            Eq(pdpNoMask.Gateway, "11.210.132.29", "CGCONTRDP no-mask gateway");
            Eq(pdpNoMask.PrimaryDns, "213.87.142.95", "CGCONTRDP no-mask dns");
            var contrdpWithMask = "\r\n+CGCONTRDP: 1,5,\"internet\",\"11.210.132.30.255.255.255.252\",\"11.210.132.29\"\r\nOK";
            Ok(PdpContext.TryParseIpv4(contrdpWithMask, 1, out var pdpMask), "CGCONTRDP 8-octet local addr parsed");
            Eq(pdpMask.SubnetMask, "255.255.255.252", "CGCONTRDP real mask kept");
            var contrdpZeroMask = "\r\n+CGCONTRDP: 1,5,\"\",\"11.210.132.30.0.0.0.0\",\"11.210.132.29\"\r\nOK";
            Ok(PdpContext.TryParseIpv4(contrdpZeroMask, 1, out var pdpZero), "CGCONTRDP zero mask parsed");
            Eq(pdpZero.SubnetMask, "255.255.255.255", "CGCONTRDP 0.0.0.0 mask → /32 fallback");
            Ok(!PdpContext.TryParseIpv4("\r\n+CGCONTRDP: 1,5,\"\",\"\",\"\"\r\nOK", 1, out var pdpEmpty) && pdpEmpty == null, "CGCONTRDP empty → no parameters");

            Console.WriteLine("== unit: SignalParser ==");
            var snap = SignalParser.Parse(
                "\r\n+RSRP: 116, 3250, -93\r\nOK",
                "+CSQ: 10, 99\r\nOK",
                "+GTCCINFO: \r\n1,4,250,1,91DC,00111EE03,3250,116,107,100,14,47,47,22\r\nOK",
                "+GTCAINFO: PCC:107,116,3250,100,100,1,1,1,3,-93 SCC 1:2,0,101,491,400,50,255,0,255,0,255,-93 SCC 2:2,0,103,470,1725,75,255,0,255,0,255,-93\r\nOK",
                null);
            Ok(snap.HasSignal, "snapshot has signal");
            Ok(snap.Rsrp == -93 && snap.Pci == 116 && snap.Earfcn == 3250, "snapshot rsrp/pci/earfcn");
            Eq(snap.Band, "B7", "snapshot band");
            Ok(Math.Abs(snap.RsrqDb - (-8.5)) < 0.01, "snapshot rsrq decode");
            Eq(snap.SinrIdx, "14", "snapshot sinr idx");
            Ok(snap.Carriers.Count == 3, "CA 3 carriers");
            Eq(snap.Carriers[0], "PCC B7 20MHz", "CA PCC label");
            Eq(snap.Carriers[1], "SCC 1 B1 10MHz", "CA SCC1 label");
            Eq(snap.Carriers[2], "SCC 2 B3 15MHz", "CA SCC2 label");
            var caDeact = SignalParser.Parse(null, null, null, "+GTCAINFO: PCC:107,116,3250,100,100,1,1,1,3,-93 SCC 1:1,0,101,491,400,50,255,0,255,0,255,-93 SCC 2:2,0,103,470,1725,75,255,0,255,0,255,-93\r\nOK", null);
            Ok(caDeact.Carriers.Count == 2 && caDeact.Carriers[1] == "SCC 2 B3 15MHz", "CA deactivated SCC skipped (E35)");
            var tSnap = SignalParser.Parse(null, null, null, null, "\r\n+GTSENRDTEMP: 0,45\r\nOK");
            Eq(tSnap.TempC, "45", "GTSENRDTEMP temp из поля 1 (E32)");
            var tSnap1 = SignalParser.Parse(null, null, null, null, "\r\n+GTSENRDTEMP: 0\r\nOK");
            Ok(tSnap1.TempC == null, "GTSENRDTEMP single-field → температуры нет (E32)");

            Console.WriteLine("== unit: BandPlan ==");
            Eq(BandPlan.BandFromEarfcn(400), "B1", "earfcn 400");
            Eq(BandPlan.BandFromEarfcn(1725), "B3", "earfcn 1725");
            Eq(BandPlan.BandFromEarfcn(3250), "B7", "earfcn 3250");
            Eq(BandPlan.BandFromEarfcn(38100), "B38", "earfcn 38100");
            Eq(BandPlan.BuildGtact(BandPlan.RAT_AUTO, BandPlan.MtsTrioLte, BandPlan.NrAll),
               "AT+GTACT=20,6,3,1,2,4,5,8,101,103,107,501,502,503,505,507,508,5020,5025,5028,5030,5038,5040,5041,5048,5066,5071,5077,5078,5079",
               "BuildGtact trio == device-accepted string");
            int[] rat; List<int> lte, nr;
            BandPlan.ParseGtact("+GTACT: 20,6,3,1,2,4,5,8,101,103,107,501,5020,5079\r\nOK", out rat, out lte, out nr);
            Ok(rat[0] == 20 && lte.Count == 3 && lte.Contains(3) && nr.Contains(1) && nr.Contains(20) && nr.Contains(79), "ParseGtact roundtrip");
            Ok(BandPlan.RAT_LTE[0] == 2 && BandPlan.RAT_3G[0] == 1 && BandPlan.RAT_5GSA[0] == 14
                && BandPlan.RAT_AUTO[0] == 20 && BandPlan.RAT_5G4G[0] == 17, "RAT presets rat[0] per §11.1.14 (E33)");
            Ok(BandPlan.RAT_LTE[1] == 3 && BandPlan.RAT_LTE[2] == 3 && BandPlan.RAT_3G[1] == 2 && BandPlan.RAT_3G[2] == 2
                && BandPlan.RAT_5GSA[1] == 6 && BandPlan.RAT_5GSA[2] == 6, "RAT presets triples per §11.1.14 (E33)");
            Ok(BandPlan.NrCode(9) == 509 && BandPlan.NrCode(10) == 5010, "NrCode 9→509, 10→5010 per §11.1.14 (E34)");

            int[] ratN; List<int> lteN, nrN;
            BandPlan.ParseGtact("+GTACT: 2,3,3,101,103,107\r\nOK", out ratN, out lteN, out nrN);
            Ok(lteN.Count == 3 && lteN.Contains(1) && lteN.Contains(3) && lteN.Contains(7), "ParseGtact без фикс-поля: B1 не съедается");


            Console.WriteLine("== unit: ModemSettingsService ==");
            var settingsTransport = FakeTransport.StandardFm350()
                .On("AT+CGDCONT?", "\r\n+CGDCONT: 1,\"IP\",\"saved.apn\"\r\n")
                .On("AT+E5GOPT?", "\r\n+E5GOPT: 7\r\n");
            var settingsModem = new Modem();
            Ok(settingsModem.Open(settingsTransport, "SETTINGS"), "settings modem opens");
            var settings = new ModemSettingsService(settingsModem).ReadInitial();
            Ok(settings.Bands.HasValues && settings.Bands.Lte.Length > 0 && settings.Bands.Nr.Length > 0, "initial bands loaded");
            Ok(settings.E5gOption == 7, "initial 5G option loaded");
            Ok(settings.Pdp.Type == "IP" && settings.Pdp.Apn == "saved.apn", "saved APN from CGDCONT");
            settingsModem.Close();
            Throws<ArgumentException>(() => new SerialTransport("not-a-com-port"), "serial transport rejects invalid port name");
            Eq(InfoDecode.Human("x", new List<string>()), "OK", "info decoder tolerates short command");

            Console.WriteLine("== unit: ApduOverAt ==");
            var apduTransport = new FakeTransport()
                .On("AT", "")
                .On("AT+CCHO=\"A0000005591010FFFFFFFF8900000100\"", "\r\n+CCHO: 1\r\n")
                .On("AT+CCHC=1", "");
            var apduModem = new Modem();
            Ok(apduModem.Open(apduTransport, "APDU"), "apdu modem opens");
            var apdu = new ApduOverAt(apduModem);
            Ok(apdu.LogicChannelOpen("A0000005591010FFFFFFFF8900000100") == 1, "CCHO opens channel");
            apduTransport.Sent.Clear();
            Ok(apdu.LogicChannelOpen("A0000005591010FFFFFFFF8900000100") == 1, "CCHO re-opens after stale close");
            Ok(apduTransport.Sent.Count == 2 && apduTransport.Sent[0] == "AT+CCHC=1"
                && apduTransport.Sent[1].StartsWith("AT+CCHO"), "stale channel closed before new CCHO");
            apduTransport.Sent.Clear();
            Ok(apdu.Disconnect() && apduTransport.Sent.Count == 1 && apduTransport.Sent[0] == "AT+CCHC=1",
                "Disconnect sends CCHC for an open channel");
            apduTransport.Sent.Clear();
            Ok(apdu.Disconnect() && apduTransport.Sent.Count == 0, "Disconnect without channel is a no-op");
            apduModem.Close();

            var retryTransport = new FakeTransport()
                .On("AT", "")
                .On("AT+CCHO=\"A0000005591010FFFFFFFF8900000100\"", "\r\n+CCHO: 2\r\n");
            var retryModem = new Modem();
            Ok(retryModem.Open(retryTransport, "APDU2"), "apdu retry modem opens");
            var retryApdu = new ApduOverAt(retryModem);
            retryTransport.Sent.Clear();
            retryTransport.FailNextWrites = 2;
            Ok(retryApdu.LogicChannelOpen("A0000005591010FFFFFFFF8900000100") == 2, "CCHO retried until success");
            Eq(retryTransport.Sent.Count.ToString(), "3", "CCHO attempts: 3");
            retryModem.Close();

            Console.WriteLine("== unit: Lang ==");
            int missing = 0;
            var table = typeof(Lang).GetField("Table", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                                   .GetValue(null) as Dictionary<string, string[]>;
            foreach (var kv in table)
            {
                if (kv.Value.Length != 2 || string.IsNullOrWhiteSpace(kv.Value[0]) || string.IsNullOrWhiteSpace(kv.Value[1]))
                {
                    missing++;
                    Console.WriteLine("   missing translation: " + kv.Key);
                }
            }
            Ok(missing == 0 && table.Count > 0, "Lang: all keys have RU+EN, table non-empty");

            Console.WriteLine("== unit: LogBuffer ==");
            var logBuffer = new LogBuffer();
            System.Threading.Tasks.Parallel.For(0, 100, i => logBuffer.Append("line " + i));
            var logBatch = logBuffer.Drain();
            Ok(logBatch.EntryCount == 50, "LogBuffer retains 50 entries");
            Ok(logBatch.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 50, "LogBuffer bounded text snapshot");
            Ok(logBuffer.Drain().Text == null, "LogBuffer unchanged snapshot skipped");

            Console.WriteLine("== integration: full modem session over FakeTransport ==");
            var ft = FakeTransport.StandardFm350();
            var modem = new Modem();
            Ok(modem.Open(ft, "FAKE"), "open via fake transport");
            Ok(modem.IsOpen, "isOpen");

            var pin = modem.Send("AT+CPIN?");
            Ok(pin.Contains("READY"), "CPIN READY");
            modem.Send("AT+COPS=0", 180000);
            var imsi = Modem.Number(modem.Send("AT+CIMI"));
            Eq(imsi, "250015893949774", "IMSI parsed");
            var cg = modem.Send("AT+CGDCONT=1,\"IP\",\"internet.mts.ru\"");
            Ok(cg.Contains("OK"), "CGDCONT accepted");
            string pdnUrc = null;
            modem.OnUrc += u => { if (u.Contains("PDN ACT")) pdnUrc = u; };
            var act = modem.Send("AT+CGACT=1,1", 30000);
            Ok(act.Contains("OK") && pdnUrc != null, "PDN activated (OK + CGEV URC routed)");
            var addr = modem.Send("AT+CGPADDR=1; +GTDNS=1");
            var ipm = System.Text.RegularExpressions.Regex.Match(addr, @"\+CGPADDR:\s*1,""([\d.]+)""");
            Ok(ipm.Success && ipm.Groups[1].Value == "11.24.224.123", "PDN IP parsed");
            var dnsm = System.Text.RegularExpressions.Regex.Match(addr, @"\+GTDNS:\s*1,""([\d.]+)""(?:,""([\d.]+)"")?");
            Ok(dnsm.Success && dnsm.Groups[1].Value == "213.87.142.95" && dnsm.Groups[2].Success && dnsm.Groups[2].Value == "213.87.142.94", "DNS parsed (primary + secondary)");

            var pollingService = new SignalPollingService(modem);
            var pollResult = pollingService.Read(false, 1, false);
            var snap2 = pollResult.Signal;
            Ok(pollResult.PdnResponse == null, "poll excludes PDN query when disabled");
            Ok(snap2.HasSignal && snap2.Rsrp == -93, "poll snapshot");
            Ok(snap2.Carriers.Count == 3, "poll CA");
            var commandCount = ft.Sent.Count;
            pollingService.ReadHealth(1, true);
            Ok(ft.Sent.Count == commandCount + 1 && ft.Sent[ft.Sent.Count - 1] == "AT+CGPADDR=1", "background poll uses one health command");

            int[] rat2; List<int> lte2, nr2;
            BandPlan.ParseGtact(modem.Send("AT+GTACT?"), out rat2, out lte2, out nr2);
            Ok(lte2.Contains(38) && nr2.Contains(78), "device band read");

            Eq(Modem.Number(modem.Send("AT+CGSN")), "352455106006257", "CGSN IMEI (E39)");
            Ok(modem.Send("ATI").Contains("FM350-GL"), "ATI model (E39)");
            Ok(modem.Send("AT+COPS?").Contains("25001"), "COPS? MCCMNC (E39)");
            Ok(modem.Send("AT+GTRFHWVER?").Contains("V1.0.1"), "GTRFHWVER (E39)");
            Ok(modem.Send("AT+ECAL?").Contains("+ECAL: 1"), "ECAL non-empty (E39)");
            Ok(modem.Send("AT+GTQUERYCALI?").Contains("+GTQUERYCALI: 0"), "GTQUERYCALI non-empty (E39)");

            modem.Close();
            Ok(!modem.IsOpen, "close");

            Ok(ft.Sent.Count >= 4 && ft.Sent[0] == "AT" && ft.Sent[1] == "AT+CPIN?" && ft.Sent[2] == "AT+COPS=0" && ft.Sent[3] == "AT+CIMI", "session command sequence");
            Ok(ft.Sent.Contains("AT+CGACT=1,1"), "session contains activation command");

            Console.WriteLine("== edge cases ==");

            var ft2 = FakeTransport.StandardFm350();
            ft2.Chunked = true;
            var m2 = new Modem();
            Ok(m2.Open(ft2, "FAKE2"), "chunked open");
            var r2 = m2.Send("AT+RSRP?");
            Ok(r2.Contains("+RSRP: 116, 3250, -93"), "fragmented response assembled");

            var ft6 = FakeTransport.StandardFm350();
            ft6.Chunked = true;
            ft6.ChunkAt = ("AT+CSQ\r" + "\r\n+CSQ: 10, 99\r\n" + "\r\nOK\r\n").Length - 3;
            var m6 = new Modem();
            Ok(m6.Open(ft6, "FAKE6"), "open FAKE6");
            var r6 = m6.Send("AT+CSQ");
            Ok(r6.Contains("+CSQ: 10") && r6.Contains("OK"), "final code split O|K assembled");

            var ft7 = new FakeTransport().On("AT", "").On("AT+BAD", "\r\n+CME ERROR: 50\r\n");
            ft7.Chunked = true;
            ft7.ChunkAt = ("AT+BAD\r" + "\r\n+CME ERROR: 5").Length;
            var m7 = new Modem();
            Ok(m7.Open(ft7, "FAKE7"), "open FAKE7");
            var r7 = m7.Send("AT+BAD");
            Ok(r7.Contains("+CME ERROR: 50"), "CME ERROR split inside digits not truncated (C40)");

            var ft3 = FakeTransport.StandardFm350();
            var m3 = new Modem();
            string gotUrc = null;
            m3.OnUrc += u => gotUrc = u;
            Ok(m3.Open(ft3, "FAKE3"), "open FAKE3");
            ft3.InjectNext = "\r\n+CGEV: ME PDN ACT 1\r\n";
            var r3 = m3.Send("AT+CSQ");
            Ok(gotUrc != null && gotUrc.StartsWith("+CGEV"), "URC routed to event");
            Ok(!r3.Contains("+CGEV"), "URC removed from response body");
            Ok(r3.Contains("+CSQ: 10"), "response body intact after URC");

            var ft4 = FakeTransport.StandardFm350();
            var m4 = new Modem();
            m4.Open(ft4, "FAKE4");
            var r4 = m4.Send("AT+UNKNOWNCMD");
            Ok(r4.Contains("ERROR"), "ERROR surfaced");

            var ft5 = FakeTransport.StandardFm350();
            var m5 = new Modem();
            m5.Open(ft5, "FAKE5");
            ft5.Silent = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r5 = m5.Send("AT+CSQ", 700);
            sw.Stop();
            Ok(r5.Length == 0 && sw.ElapsedMilliseconds < 1500, "timeout returns empty within bound");

            var closeTransport = FakeTransport.StandardFm350();
            var closeModem = new Modem();
            Ok(closeModem.Open(closeTransport, "FAKE-CLOSE"), "open before concurrent close");
            closeTransport.Silent = true;
            string closeResponse = null;
            Exception closeError = null;
            var sendThread = new Thread(() =>
            {
                try { closeResponse = closeModem.Send("AT+COPS=0", 10000, slowCommand: true); }
                catch (Exception exception) { closeError = exception; }
            });
            sendThread.Start();
            SpinWait.SpinUntil(() => closeTransport.Sent.Count >= 2, 1000);
            var closeWatch = System.Diagnostics.Stopwatch.StartNew();
            closeModem.Close();
            var sendStopped = sendThread.Join(1500);
            closeWatch.Stop();
            Ok(sendStopped && closeError == null && closeResponse == string.Empty && !closeModem.IsOpen && closeWatch.ElapsedMilliseconds < 1500,
                "close interrupts active modem command");

            var ex = new SerialExecutor();
            var order = new List<int>();
            for (int i = 0; i < 5; i++) { int k = i; ex.Post(() => { Thread.Sleep(20); lock (order) order.Add(k); }); }

            var waitUntil = DateTime.UtcNow.AddSeconds(3);
            while (ex.Pending > 0 && DateTime.UtcNow < waitUntil) Thread.Sleep(20);
            ex.Dispose();
            bool fifo = order.Count == 5;
            for (int i = 0; i < order.Count; i++) if (order[i] != i) fifo = false;
            Ok(ex.Pending == 0 && fifo, "executor FIFO order + Pending==0 after drain");

            Console.WriteLine("== edge cases round 2 ==");
            Eq(BandPlan.BandFromEarfcn(0), "B1", "earfcn 0");
            Eq(BandPlan.BandFromEarfcn(599), "B1", "earfcn 599");
            Eq(BandPlan.BandFromEarfcn(600), "B2", "earfcn 600");
            Eq(BandPlan.BandFromEarfcn(6150), "B20", "earfcn 6150 (B20 start)");
            Eq(BandPlan.BandFromEarfcn(6449), "B20", "earfcn 6449 (B20 end)");
            Eq(BandPlan.BandFromEarfcn(9210), "B28", "earfcn 9210 (B28 start)");
            Eq(BandPlan.BandFromEarfcn(38250), "B39", "earfcn 38250 = B39 start");
            Eq(BandPlan.BandFromEarfcn(38650), "B40", "earfcn 38650 = B40 start");
            Eq(BandPlan.BandFromEarfcn(6600), "B22", "earfcn 6600 = B22 start");
            Eq(BandPlan.BandFromEarfcn(6750), "B22", "earfcn 6750 = B22 (D25)");
            Eq(BandPlan.BandFromEarfcn(7399), "B22", "earfcn 7399 = B22 end (D25)");
            Eq(BandPlan.BandFromEarfcn(7400), "B?", "earfcn 7400 — диапазон не назначен (D25)");
            Eq(BandPlan.BandFromEarfcn(7500), "B23", "earfcn 7500 = B23 start (D25)");
            Eq(BandPlan.BandFromEarfcn(7699), "B23", "earfcn 7699 = B23 end (D25)");
            Eq(BandPlan.BandFromEarfcn(7700), "B24", "earfcn 7700 = B24 start (D25)");
            Eq(BandPlan.BandFromEarfcn(8039), "B24", "earfcn 8039 = B24 end (D25)");
            Eq(BandPlan.BandFromEarfcn(8040), "B25", "earfcn 8040 = B25 start");
            Eq(BandPlan.BandFromEarfcn(36000), "B33", "earfcn 36000 = B33 start");
            Eq(BandPlan.BandFromEarfcn(36200), "B34", "earfcn 36200 = B34 start (K11)");
            Eq(BandPlan.BandFromEarfcn(36250), "B34", "earfcn 36250 ∈ B34 (K11: старт B34 = 36200)");
            Eq(BandPlan.BandFromEarfcn(20000), "B?", "earfcn 20000 — диапазон не назначен (C18)");
            Eq(BandPlan.BandFromEarfcn(5000), "B?", "earfcn 5000 — диапазон не назначен (H11)");
            Eq(BandPlan.BandFromEarfcn(5600), "B?", "earfcn 5600 — диапазон не назначен (H11)");
            Eq(BandPlan.BandFromEarfcn(46000), "B?", "earfcn 46000 — B44/B45 устройством не поддерживаются (H11)");
            Eq(BandPlan.BandFromEarfcn(9900), "B?", "earfcn 9900 — B31 устройством не поддерживается (J3)");
            Eq(BandPlan.BandFromEarfcn(9920), "B32", "earfcn 9920 = B32 start");
            Eq(BandPlan.BandFromEarfcn(68000), "B?", "earfcn 68000 — B67-B70 устройством не поддерживаются (J3)");
            Eq(BandPlan.BandFromEarfcn(68586), "B71", "earfcn 68586 = B71 start");
            var ftF = FakeTransport.StandardFm350();
            var mF = new Modem();
            int lostCount = 0;
            mF.OnPortLost += () => lostCount++;
            Ok(mF.Open(ftF, "FAKE-F"), "open before fail");
            ftF.FailOnWrite = true;
            var rF = mF.Send("AT+CSQ");
            Ok(rF.Length == 0 && !mF.IsOpen && lostCount == 1, "IOException → close + OnPortLost x1");
            var ftS = FakeTransport.StandardFm350();
            var mS = new Modem();
            int lostS = 0;
            mS.OnPortLost += () => lostS++;
            Ok(mS.Open(ftS, "FAKE-S"), "open FAKE-S");
            ftS.Silent = true;
            mS.Send("AT+CSQ", 300);
            Ok(lostS == 0 && mS.IsOpen, "silent x1: still alive");
            mS.Send("AT+CSQ", 300);
            mS.Send("AT+CSQ", 300);
            Ok(lostS == 1 && !mS.IsOpen, "silent x3 → port lost");

            var ftL = FakeTransport.StandardFm350();
            var mL = new Modem();
            int lostL = 0;
            mL.OnPortLost += () => lostL++;
            Ok(mL.Open(ftL, "FAKE-L"), "open FAKE-L");
            ftL.Silent = true;
            mL.Send("AT+COPS=0", 300, slowCommand: true);
            mL.Send("AT+CGACT=1,1", 300, slowCommand: true);
            mL.Send("AT+COPS=0", 300, slowCommand: true);
            Ok(lostL == 0 && mL.IsOpen, "slowCommand x3: порт жив");
            mL.Send("AT+CSQ", 300);
            mL.Send("AT+CSQ", 300);
            mL.Send("AT+CSQ", 300);
            Ok(lostL == 1 && !mL.IsOpen, "обычные x3 после slow x3 → port lost");
            var junk = SignalParser.Parse("", null, "ERROR", "garbage", null);
            Ok(!junk.HasSignal && junk.Carriers.Count == 0 && double.IsNaN(junk.RsrqDb), "parser survives garbage");
            var csq99 = SignalParser.Parse(null, "+CSQ: 99, 99\r\nOK", null, null, null);
            Ok(csq99.Csq == -1, "CSQ 99 = unknown");
            var hugeRsrp = SignalParser.Parse("\r\n+RSRP: 116, 3250, -9999999999999\r\nOK", null, null, null, null);
            Ok(!hugeRsrp.HasSignal, "13-digit RSRP → no-signal tick без OverflowException (D28)");
            var ca4 = SignalParser.Parse(null, null, null, "+GTCAINFO: PCC:107,116,3250,100,100,1,1,1,3,-93 SCC 1:2,0,103,470,1725,75,255,0,255,0,255,-93 SCC 10:2,0,101,491,400,50,255,0,255,0,255,-93\r\nOK", null);
            Ok(ca4.Carriers.Count == 3, "SCC 10 (two-digit) parsed");

            Console.WriteLine("== integration: proxy loopback ==");

            var echo = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            echo.Start();
            var echoPort = ((System.Net.IPEndPoint)echo.LocalEndpoint).Port;
            var echoThread = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var c = echo.AcceptTcpClient();
                        new Thread(() =>
                        {
                            try
                            {
                                var s = c.GetStream();
                                var b = new byte[4096];
                                int n;
                                while ((n = s.Read(b, 0, b.Length)) > 0) s.Write(b, 0, n);
                            }
                            catch { }
                            try { c.Close(); } catch { }
                        })
                        { IsBackground = true }.Start();
                    }
                }
                catch { }
            })
            { IsBackground = true };
            echoThread.Start();

            var proxy = new ProxyEngine();
            proxy.Start(0, "127.0.0.1", System.Net.NetworkInformation.NetworkInterface.LoopbackInterfaceIndex);
            Throws<ArgumentOutOfRangeException>(() => proxy.SetUpstream("127.0.0.1", 70000), "proxy rejects invalid upstream port");
            Throws<ArgumentException>(() => proxy.SetUpstream("bad\r\nhost", 80), "proxy rejects CRLF upstream host");

            using (var idle = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            using (var active = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                active.ReceiveTimeout = 2000;
                var stream = active.GetStream();
                var request = Encoding.ASCII.GetBytes("GET http://127.0.0.1:" + echoPort + "/parallel HTTP/1.0\r\n\r\n");
                stream.Write(request, 0, request.Length);
                var response = new byte[512];
                var count = 0;
                try { count = stream.Read(response, 0, response.Length); } catch { }
                Ok(count > 0 && Encoding.ASCII.GetString(response, 0, count).Contains("GET /parallel"),
                    "idle proxy client does not block accept loop");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                var req = Encoding.ASCII.GetBytes("GET http://127.0.0.1:" + echoPort + "/hello HTTP/1.0\r\n\r\n");
                st.Write(req, 0, req.Length);
                var rb = new byte[512];
                int rn = st.Read(rb, 0, rb.Length);
                var resp = Encoding.ASCII.GetString(rb, 0, rn);
                Ok(resp.Contains("GET /hello"), "proxy HTTP absolute→origin form (echo got origin form)");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                var req = Encoding.ASCII.GetBytes("get http://127.0.0.1:" + echoPort + "/lower HTTP/1.0\r\n\r\n");
                st.Write(req, 0, req.Length);
                var rb = new byte[512];
                int rn = st.Read(rb, 0, rb.Length);
                var resp = Encoding.ASCII.GetString(rb, 0, rn);
                Ok(resp.Contains("get /lower"), "proxy lowercase get → proxied (D39)");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                var req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX http://127.0.0.1:" + echoPort + "/\r\n\r\n");
                st.Write(req, 0, req.Length);
                var rb = new byte[64];
                int rn;
                try { rn = st.Read(rb, 0, rb.Length); } catch { rn = 0; }
                Ok(rn > 0 && Encoding.ASCII.GetString(rb, 0, rn).Contains("400"), "origin-form + malformed header → 400 Bad Request, not proxied (P2)");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                st.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                var gr = new byte[2];
                int got = 0;
                while (got < 2) { int n = st.Read(gr, got, 2 - got); if (n <= 0) break; got += n; }
                Ok(gr[0] == 0x05 && gr[1] == 0x00, "socks5 greeting");
                var req = new byte[] { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)echoPort };
                st.Write(req, 0, req.Length);
                var rr = new byte[10];
                got = 0;
                while (got < 10) { int n = st.Read(rr, got, 10 - got); if (n <= 0) break; got += n; }
                Ok(rr[1] == 0x00, "socks5 connect granted");
                var ping = Encoding.ASCII.GetBytes("ping-through-socks");
                st.Write(ping, 0, ping.Length);
                var pb = new byte[64];
                int pn = st.Read(pb, 0, pb.Length);
                Ok(Encoding.ASCII.GetString(pb, 0, pn) == "ping-through-socks", "socks5 relay works");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                st.Write(new byte[] { 0x05, 0x00 }, 0, 2);
                var gr = new byte[2];
                int got = 0;
                while (got < 2) { int n = st.Read(gr, got, 2 - got); if (n <= 0) break; got += n; }
                int eof;
                try { eof = st.Read(gr, 0, 2); } catch { eof = -1; }
                Ok(got == 2 && gr[0] == 0x05 && gr[1] == 0xFF && eof <= 0, "socks5 NMETHODS=0 → 05 FF + close (B16)");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                st.Write(new byte[] { 0x05, 0x01, 0x02 }, 0, 3);
                var gr = new byte[2];
                int got = 0;
                while (got < 2) { int n = st.Read(gr, got, 2 - got); if (n <= 0) break; got += n; }
                int eof;
                try { eof = st.Read(gr, 0, 2); } catch { eof = -1; }
                Ok(got == 2 && gr[0] == 0x05 && gr[1] == 0xFF && eof <= 0, "socks5 no no-auth method → 05 FF + close (B16)");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                st.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                var gr = new byte[2];
                int got = 0;
                while (got < 2) { int n = st.Read(gr, got, 2 - got); if (n <= 0) break; got += n; }
                var req = new byte[] { 0x04, 0x01, 0x00, 0x01, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)echoPort };
                st.Write(req, 0, req.Length);
                var rr = new byte[10];
                got = 0;
                while (got < 10) { int n = st.Read(rr, got, 10 - got); if (n <= 0) break; got += n; }
                Ok(got == 10 && rr[0] == 0x05 && rr[1] == 0x01, "socks5 request VER=0x04 → failure REP (C9)");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                st.Write(new byte[] { 0x05, 0x01, 0x00 }, 0, 3);
                var gr = new byte[2];
                int got = 0;
                while (got < 2) { int n = st.Read(gr, got, 2 - got); if (n <= 0) break; got += n; }
                var req = new byte[] { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, 0, 1 };
                st.Write(req, 0, req.Length);
                var rr = new byte[10];
                got = 0;
                while (got < 10) { int n = st.Read(rr, got, 10 - got); if (n <= 0) break; got += n; }
                Ok(got == 10 && rr[1] != 0x00, "socks5 connect to closed port → failure REP (C39)");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                var req = Encoding.ASCII.GetBytes("CONNECT 127.0.0.1:1 HTTP/1.1\r\n\r\n");
                st.Write(req, 0, req.Length);
                var rb = new byte[64];
                int rn = st.Read(rb, 0, rb.Length);
                Ok(Encoding.ASCII.GetString(rb, 0, rn).StartsWith("HTTP/1.1 502"), "http CONNECT to closed port → 502 (K17)");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                var req = Encoding.ASCII.GetBytes("GET http://127.0.0.1:1/ HTTP/1.0\r\n\r\n");
                st.Write(req, 0, req.Length);
                var rb = new byte[64];
                int rn = st.Read(rb, 0, rb.Length);
                Ok(Encoding.ASCII.GetString(rb, 0, rn).StartsWith("HTTP/1.1 502"), "http GET to closed port → 502 (K17)");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                var seg = new byte[3 + 10 + 13];
                seg[0] = 0x05; seg[1] = 0x01; seg[2] = 0x00;
                seg[3] = 0x05; seg[4] = 0x01; seg[5] = 0x00; seg[6] = 0x01;
                seg[7] = 127; seg[8] = 0; seg[9] = 0; seg[10] = 1;
                seg[11] = (byte)(echoPort >> 8); seg[12] = (byte)echoPort;
                var early = Encoding.ASCII.GetBytes("early-payload");
                Array.Copy(early, 0, seg, 13, early.Length);
                st.Write(seg, 0, seg.Length);
                var gr = new byte[2];
                int got = 0;
                while (got < 2) { int n = st.Read(gr, got, 2 - got); if (n <= 0) break; got += n; }
                Ok(gr[0] == 0x05 && gr[1] == 0x00, "socks5 one-segment greeting");
                var rr = new byte[10];
                got = 0;
                while (got < 10) { int n = st.Read(rr, got, 10 - got); if (n <= 0) break; got += n; }
                Ok(rr[1] == 0x00, "socks5 one-segment connect granted");
                var pb = new byte[64];
                int pn = st.Read(pb, 0, pb.Length);
                Ok(Encoding.ASCII.GetString(pb, 0, pn) == "early-payload", "one-segment early payload relayed");
            }

            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                var fat = new byte[600];
                for (int i = 0; i < fat.Length; i++) fat[i] = (byte)('A' + i % 26);
                var seg = new byte[3 + 10 + fat.Length];
                seg[0] = 0x05; seg[1] = 0x01; seg[2] = 0x00;
                seg[3] = 0x05; seg[4] = 0x01; seg[5] = 0x00; seg[6] = 0x01;
                seg[7] = 127; seg[8] = 0; seg[9] = 0; seg[10] = 1;
                seg[11] = (byte)(echoPort >> 8); seg[12] = (byte)echoPort;
                Array.Copy(fat, 0, seg, 13, fat.Length);
                st.Write(seg, 0, seg.Length);
                var gr = new byte[2];
                int got = 0;
                while (got < 2) { int n = st.Read(gr, got, 2 - got); if (n <= 0) break; got += n; }
                Ok(gr[0] == 0x05 && gr[1] == 0x00, "fat one-segment greeting");
                var rr = new byte[10];
                got = 0;
                while (got < 10) { int n = st.Read(rr, got, 10 - got); if (n <= 0) break; got += n; }
                Ok(rr[1] == 0x00, "fat one-segment connect granted");
                var back = new byte[fat.Length];
                got = 0;
                while (got < back.Length) { int n = st.Read(back, got, back.Length - got); if (n <= 0) break; got += n; }
                bool same = got == fat.Length;
                for (int i = 0; same && i < fat.Length; i++) same = back[i] == fat[i];
                Ok(same, "fat early payload relayed intact");
            }

            var ups = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            ups.Start();
            var upsPort = ((System.Net.IPEndPoint)ups.LocalEndpoint).Port;
            var upsThread = new Thread(() =>
            {
                try
                {
                    var c = ups.AcceptTcpClient();
                    var s = c.GetStream();
                    var hb = new byte[4096];
                    int hlen = 0;

                    while (hlen < 4 || hb[hlen - 4] != 13 || hb[hlen - 3] != 10 || hb[hlen - 2] != 13 || hb[hlen - 1] != 10)
                    {
                        int n = s.Read(hb, hlen, hb.Length - hlen);
                        if (n <= 0) break;
                        hlen += n;
                    }
                    var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n\r\nSSH-2.0-fake-banner\r\n");
                    s.Write(reply, 0, reply.Length);
                    int m;
                    while ((m = s.Read(hb, 0, hb.Length)) > 0) s.Write(hb, 0, m);
                }
                catch { }
                try { ups.Stop(); } catch { }
            })
            { IsBackground = true };
            upsThread.Start();
            proxy.SetUpstream("127.0.0.1", upsPort);
            using (var cl = new System.Net.Sockets.TcpClient("127.0.0.1", proxy.Port))
            {
                cl.ReceiveTimeout = 5000;
                var st = cl.GetStream();
                var req = Encoding.ASCII.GetBytes("CONNECT 127.0.0.1:" + echoPort + " HTTP/1.1\r\n\r\n");
                st.Write(req, 0, req.Length);
                var need = "HTTP/1.1 200 Connection established\r\n\r\nSSH-2.0-fake-banner\r\n";
                var rb = new byte[512];
                int got = 0;
                while (got < need.Length)
                {
                    int n;
                    try { n = st.Read(rb, got, rb.Length - got); } catch { break; }
                    if (n <= 0) break;
                    got += n;
                }
                Eq(Encoding.ASCII.GetString(rb, 0, got), need, "B18: upstream early bytes (banner) before relay");
                var ping = Encoding.ASCII.GetBytes("via-upstream");
                st.Write(ping, 0, ping.Length);
                int pn;
                try { pn = st.Read(rb, 0, rb.Length); } catch { pn = 0; }
                Ok(pn == ping.Length && Encoding.ASCII.GetString(rb, 0, pn) == "via-upstream", "B18: relay through upstream works");
            }
            proxy.SetUpstream(null, 0);
            ups.Stop();

            proxy.Stop();
            echo.Stop();

            Console.WriteLine("== N1: GTCCINFO rat-aware (§11.1.15) ==");
            var nrSnap = SignalParser.Parse(null, null, "+GTCCINFO: \r\n1,9,250,1,91DC,00111EE03,632736,116,5078,100,25,47,47,10\r\nOK", null, null);
            Ok(Math.Abs(nrSnap.RsrqDb - (-38.5)) < 0.01 && nrSnap.SinrIdx == "25", "NR: rsrq = cf[13]×0.5−43.5, sinr = cf[10]");
            Ok(Math.Abs(nrSnap.SinrDb - (-10.5)) < 0.01, "NR sinr dB = 25/2−23 (TS 38.133)");
            var nr255 = SignalParser.Parse(null, null, "+GTCCINFO: \r\n1,9,250,1,91DC,00111EE03,632736,116,5078,100,255,47,47,10\r\nOK", null, null);
            Ok(nr255.SinrIdx == null && Math.Abs(nr255.RsrqDb - (-38.5)) < 0.01, "NR sinr=255 → null, rsrq остаётся");
            var wc = SignalParser.Parse(null, null, "+GTCCINFO: \r\n1,2,250,1,91DC,00111EE03,10752,116,3250,100,75,47,47,0,0\r\nOK", null, null);
            Ok(double.IsNaN(wc.RsrqDb) && wc.SinrIdx == null, "WCDMA: ни rsrq, ни sinr");
            var lte255 = SignalParser.Parse(null, null, "+GTCCINFO: \r\n1,4,250,1,91DC,00111EE03,3250,116,107,100,255,47,47,22\r\nOK", null, null);
            Ok(Math.Abs(lte255.RsrqDb - (-8.5)) < 0.01 && lte255.SinrIdx == null, "LTE sinr=255 → null, rsrq прежний −8.5");
            Ok(double.IsNaN(lte255.SinrDb) && double.IsNaN(nr255.SinrDb), "sinr=255 → SinrDb NaN (LTE и NR)");
            var lteRq255 = SignalParser.Parse(null, null, "+GTCCINFO: \r\n1,4,250,1,91DC,00111EE03,3250,116,107,100,14,47,47,255\r\nOK", null, null);
            Ok(double.IsNaN(lteRq255.RsrqDb) && lteRq255.SinrIdx == "14", "LTE rsrq=255 → NaN (sentinel, не 108 dB)");
            Ok(Math.Abs(lteRq255.SinrDb - 7) < 0.01, "LTE sinr dB = 14/2");

            Console.WriteLine("== N2: NR band labels (§11.1.16) ==");
            var caNr = SignalParser.Parse(null, null, null, "+GTCAINFO: PCC:5078,116,632736,500,500,1,1,1,3,-93 SCC 1:2,0,501,491,400,25,25,255,0,255,0,255,-93\r\nOK", null);
            Ok(caNr.Carriers.Count == 2 && caNr.Carriers[0] == "PCC n78 100MHz" && caNr.Carriers[1] == "SCC 1 n1 5MHz", "NR CA labels n78/n1");

            Console.WriteLine("== N4: NormalizeMac ==");
            Eq(AtInput.Normalize("  internet  "), "internet", "AT input normalization trims whitespace");
            Ok(AtInput.IsSafeValue("user_name-1"), "AT value safe characters");
            Ok(!AtInput.IsSafeValue("bad;AT+CGACT=0,1"), "AT value rejects command separator");
            Ok(!AtInput.IsSafeValue("bad\r\n"), "AT value rejects control characters");
            Eq(ModemCommands.DefinePdp(1, "IP", "internet"), "AT+CGDCONT=1,\"IP\",\"internet\"", "PDP command builder");
            Eq(ModemCommands.DefinePdp(1, "IPv6", "internet"), "AT+CGDCONT=1,\"IPV6\",\"internet\"", "IPv6 PDP command builder");
            Eq(ModemCommands.DefinePdp(1, "IPv4/IPv6", "internet"), "AT+CGDCONT=1,\"IPV4V6\",\"internet\"", "dual-stack PDP command builder");
            Eq(PdpProtocol.ToDisplayValue("IPV4V6"), "IPv4/IPv6", "PDP display mapping");
            Ok(ProxyEndpoint.TryParse("192.168.1.2:9999", out var proxyHost, out var proxyPort)
                && proxyHost == "192.168.1.2" && proxyPort == 9999, "proxy endpoint parser");
            Ok(!ProxyEndpoint.TryParse("[2001:db8::1]:8080", out proxyHost, out proxyPort),
                "IPv6 proxy endpoint rejected");
            Ok(!ProxyEndpoint.TryParse("192.168.1.2", out _, out _), "proxy endpoint requires port");
            Eq(ModemCommands.SetAuthentication(1, 2, "user", "pass"), "AT+CGAUTH=1,2,\"user\",\"pass\"", "auth command builder");
            Throws<ArgumentException>(() => ModemCommands.DefinePdp(1, "IP;AT", "internet"), "PDP builder rejects invalid type");
            Throws<ArgumentException>(() => ModemCommands.SetAuthentication(1, 1, "user", "bad;AT"), "auth builder rejects injection");

            Console.WriteLine("== N5: IdentityService ==");
            const string changedImei = "352455106006258";
            var identityTransport = new FakeTransport()
                .On("AT", "")
                .On("AT+CGSN", "\r\n" + changedImei + "\r\n")
                .On("AT+EGMR=0,10", "\r\n+EGMR: \"352455103842324\"\r\n")
                .On("AT+EGMR=1,7,\"" + changedImei + "\"", "");
            var identityModem = new Modem();
            Ok(identityModem.Open(identityTransport, "FAKE-ID"), "identity modem opens");
            var identityService = new IdentityService(identityModem);
            Eq(identityService.Read(7), changedImei, "identity physical IMEI read");
            Eq(identityService.Read(10), "352455103842324", "identity eSIM IMEI read");
            var identityWrite = identityService.Write(7, changedImei);
            Ok(identityWrite.Accepted && identityWrite.VerifiedValue == changedImei, "identity write accepted and verified");
            Ok(!identityService.Write(10, changedImei).Accepted, "identity write rejection returned");
            Throws<ArgumentException>(() => identityService.Write(7, "bad;AT"), "identity write rejects injection");
            Throws<ArgumentOutOfRangeException>(() => identityService.Read(6), "identity read rejects unknown slot");
            identityModem.Close();


            Console.WriteLine("== N3: NO CARRIER (§12.2.4) ==");
            var ftNc = new FakeTransport().On("AT", "").On("AT+CGACT=1,1", "\r\nNO CARRIER\r\n");
            var mNc = new Modem();
            Ok(mNc.Open(ftNc, "FAKE-NC"), "open FAKE-NC");
            ftNc.NoOk = true;
            var swNc = System.Diagnostics.Stopwatch.StartNew();
            var rNc = mNc.Send("AT+CGACT=1,1", 5000);
            swNc.Stop();
            Ok(rNc.Contains("NO CARRIER") && !rNc.Contains("OK"), "NO CARRIER — чистый финал без OK");
            Ok(swNc.ElapsedMilliseconds < 2000, "NO CARRIER без выгорания таймаута");
            var ftNc2 = new FakeTransport().On("AT", "").On("AT+CGACT=1,1", "\r\nNO CARRIER\r\n");
            var mNc2 = new Modem();
            Ok(mNc2.Open(ftNc2, "FAKE-NC2"), "open FAKE-NC2");
            ftNc2.NoOk = true;
            ftNc2.Chunked = true;
            ftNc2.ChunkAt = ("AT+CGACT=1,1\r" + "\r\nNO CARRIER\r\n").Length - 2;
            var rNc2 = mNc2.Send("AT+CGACT=1,1");
            Ok(rNc2.Contains("NO CARRIER"), "NO CARRIER, разорванный перед финальным \\r\\n, собран");

            var correlatedTransport = new FakeTransport().On("AT", "").On("AT+CGSN", "\r\n352455106006257\r\n");
            var correlatedModem = new Modem();
            Ok(correlatedModem.Open(correlatedTransport, "FAKE-CORR"), "open response-correlation fake");
            correlatedTransport.StaleBeforeNext = "ATI\r\r\nManufacturer: Fibocom Wireless Inc.\r\nModel: FM350-GL\r\nOK\r\n";
            Eq(Modem.Number(correlatedModem.Send("AT+CGSN")), "352455106006257", "stale ATI response not assigned to CGSN");
            correlatedModem.Close();

            Console.WriteLine("== I1: InfoDecode ==");
            var infoErrorTransport = new FakeTransport().On("AT", "").On("AT+CIMI", "\r\nERROR\r\n");
            var infoErrorModem = new Modem();
            Ok(infoErrorModem.Open(infoErrorTransport, "FAKE-INFO-ERROR"), "open device-info error fake");
            Eq(new DeviceInfoService(infoErrorModem).Query("AT+CIMI", "IMSI", true).Value, "No SIM", "device info translates modem ERROR");
            infoErrorModem.Close();
            Eq(InfoDecode.Human("AT+CSQ", new List<string> { "+CSQ: 20,0" }), "-73 dBm (20/31)", "CSQ → dBm");
            Eq(InfoDecode.Human("AT+CSQ", new List<string> { "+CSQ: 99,99" }), "No signal", "CSQ 99 → No signal");
            Eq(InfoDecode.Human("AT+CIMI", null), "No SIM", "CIMI ERROR → No SIM");
            Eq(InfoDecode.Human("AT+CEREG?", new List<string> { "+CEREG: 0,1" }), "Registered (home)", "CEREG readable state");
            var cgsnInfo = InfoDecode.Human("AT+CGSN", new List<string> { "Manufacturer: Fibocom", "IMEI: 352455106006257" });
            Ok(cgsnInfo.Contains("Fibocom") && cgsnInfo.Contains("IMEI 352455106006257"), "CGSN ATI-shaped response keeps readable fields");
            Eq(InfoDecode.Human("AT+CFUN?", new List<string> { "+CFUN: 4" }), "Airplane mode", "CFUN 4");
            Eq(InfoDecode.Human("AT+ECAL?", new List<string> { "+ECAL: 1" }), "Calibrated", "ECAL 1");
            Eq(InfoDecode.Human("AT+GTQUERYCALI?", new List<string> { "+GTQUERYCALI: 0" }), "Calibration check passed", "Calibration status");
            Eq(InfoDecode.Human("AT+ESLOTSINFO?", new List<string> { "+ESLOTSINFO: 2, \"+CME ERROR: 10\", \"1\", \"0\", \"\", \"\", \"\", \"+CPIN: EMPTY_EUICC\", \"1\", \"1\", \"3B9F97C00A3FC7828031E073FE211F65D002341512810F50\", \"89033023426200000000024136483062\", \"\"" }),
                "2 SIM slots: Slot 1: no SIM; Slot 2 (eSIM): no active profile, ICCID 89033023426200000000024136483062", "SIM slots readable");
            Eq(InfoDecode.Human("AT+GTCURCAR?", new List<string> { "+GTCURCAR: 65535,\"\"" }), "Operator firmware profile: none", "carrier profile readable");
            Eq(InfoDecode.Human("AT+GTLOCKCAR?", new List<string> { "+GTLOCKCAR: 0,65535" }), "Carrier lock disabled", "carrier lock readable");
            Eq(InfoDecode.Human("AT+GTUSBMODE?", new List<string> { "+GTUSBMODE: 41" }), "RNDIS network interface", "USB mode readable");
            Eq(InfoDecode.Human("AT+GTSENRDTEMP?", new List<string> { "+GTSENRDTEMP: 0,45" }), "Sensor 0: 45 C", "Sensor temperature");
            Eq(InfoDecode.Human("AT+COPS?", new List<string> { "+COPS:0,255,\"\",0" }), "Not registered", "COPS empty oper");
            Eq(InfoDecode.Human("AT+ERAT?", new List<string> { "+ERAT: 255,0,21,0,0" }), "No service (enabled: 3G, LTE and 5G)", "ERAT 255/21");
            Eq(InfoDecode.Human("AT+E5GOPT?", new List<string> { "+E5GOPT: 7" }), "LTE + 5G SA + 5G NSA", "E5GOPT 7 (§12.2.15)");
            Eq(InfoDecode.Human("AT+GTSHUTDOWNTEMP?", new List<string> { "+GTSHUTDOWNTEMP: 1,110000", "+GTSHUTDOWNTEMP: 2,120000" }), "110–120 °C", "Shutdown temp compact range");
            Eq(InfoDecode.Human("AT+CBC", new List<string> { "+CBC: 0,3615" }), "3.62 V", "CBC → volts");
            Eq(InfoDecode.Human("AT+CCLK?", new List<string> { "+CCLK: \"26/08/13,22:14:50+00\"" }), "2026-08-13 22:14:50 UTC+00:00", "CCLK → ISO (запятая внутри кавычек)");
            Eq(InfoDecode.Human("AT+CCLK?", new List<string> { "+CCLK: \"26/08/13,22:14:50+12\"" }), "2026-08-13 22:14:50 UTC+03:00", "CCLK timezone uses 15-minute units");
            Eq(InfoDecode.Human("AT+GTBANDCFG?", new List<string> { "0,2,1", "1,3,1", "2,78,1" }), "All supported bands enabled", "bandcfg: всё вкл");
            Eq(InfoDecode.Human("AT+GTBANDCFG?", new List<string> { "0,2,1", "1,3,0", "2,78,0" }), "Disabled: B3, n78", "bandcfg: выключенные");
            Eq(InfoDecode.Human("AT+GTBANDCFG?", new List<string> { "2,80,0", "2,81,0", "2,82,0", "2,83,0", "2,84,0" }),
                "All regular bands enabled (5G uplink-only n80–n84 disabled)", "bandcfg: supplementary uplink explained");
            var gtH = InfoDecode.Human("AT+GTACT?", new List<string> { "+GTACT: 20,6,3,1,2,4,5,8,101,103,5078" });
            Eq(gtH, "Auto (5G NSA/SA + LTE + 3G), bands: 3G 4 bands, LTE 2 bands, 5G NR 1 band", "GTACT compact summary");
            Eq(InfoDecode.Human("AT+GTCAINFO?", new List<string>()), "No CA active", "CA: OK без данных");
            Eq(InfoDecode.Human("AT+GTCCINFO?", new List<string> { "+GTCCINFO:" }), "No cells", "GTCCINFO: голый префикс без данных");
            Eq(InfoDecode.Human("AT+GTCCINFO?", new List<string> { "+GTCCINFO:",
                    "1,4,250,1,91DC,00111EE09,1725,470,,,7,23,23,14",
                    "2,4,,,FFFF,00FFFFFFF,400,491,,19,19,14",
                    "2,4,,,FFFF,00FFFFFFF,3250,116,,18,18,12",
                    "2,4,,,FFFF,00FFFFFFF,38100,196,,11,11,10" }),
                "Serving: LTE B3 (1800), EARFCN 1725, PCI 470, 250-01, TAC 37340, CellID 17952265, RSRP -117 dBm, RSRQ -12.5 dB, SINR 3.5 dB\n" +
                "Neighbor: LTE B1 (2100), EARFCN 400, PCI 491, RSRP -121 dBm, RSRQ -12.5 dB\n" +
                "Neighbor: LTE B7 (2600), EARFCN 3250, PCI 116, RSRP -122 dBm, RSRQ -13.5 dB\n" +
                "Neighbor: LTE B38 (2600 TDD), EARFCN 38100, PCI 196, RSRP -129 dBm, RSRQ -14.5 dB",
                "GTCCINFO human: serving + 3 neighbors (живой трейс)");
            Eq(InfoDecode.Human("AT+GTCCINFO?", new List<string> { "1,4,250,1,91DC,00111EE03,3250,116,107,100,14,47,47,22" }),
                "Serving: LTE B7 (2600), EARFCN 3250, 20 MHz, PCI 116, 250-01, TAC 37340, CellID 17952259, RSRP -93 dBm, RSRQ -8.5 dB, SINR 7 dB",
                "GTCCINFO human: band code + bandwidth, RSRP сходится с +RSRP?");
            Eq(InfoDecode.Human("AT+GTCCINFO?", new List<string> { "1,9,250,1,91DC,00111EE03,632736,116,5078,100,25,47,47,10" }),
                "Serving: NR n78, ARFCN 632736, PCI 116, 250-01, TAC 37340, CellID 17952259, RSRP -110 dBm, RSRQ -38.5 dB, SINR -10.5 dB",
                "GTCCINFO human: NR (SS-RSRP шкала 38.133)");
            Eq(InfoDecode.Human("AT+GTCCINFO?", new List<string> { "1,2,250,1,91DC,00111EE03,10752,116,3250,100,75,47,47,0,0" }),
                "Serving: 3G, UARFCN 10752, PSC 116, 250-01, TAC 37340, CellID 17952259",
                "GTCCINFO human: 3G без измерений");
            var gtccRaw = "1,4,250,1,91DC,00111EE03,3250";
            Eq(InfoDecode.Human("AT+GTCCINFO?", new List<string> { gtccRaw }), gtccRaw, "GTCCINFO human: мусор → сырой body (fail-open)");
            Eq(InfoDecode.Human("AT+CGPADDR", new List<string> { "+CGPADDR: 1,\"11.187.104.67\",\"0.0.0.0.0.0.0.0.24.204.144.232.31.167.219.209\"" }),
                "11.187.104.67, IPv6 ::18cc:90e8:1fa7:dbd1", "CGPADDR human: IPv4 + dotted IPv6");
            Eq(InfoDecode.Human("AT+CGPADDR", new List<string> { "+CGPADDR: 1,\"10.0.0.2\"" }), "10.0.0.2", "CGPADDR human: только IPv4");
            Ok(NetConfig.DottedToIpv6("1.2.3") == null
                && NetConfig.DottedToIpv6("0.0.0.0.0.0.0.0.24.204.144.232.31.167.219.209") == "::18cc:90e8:1fa7:dbd1", "DottedToIpv6");
            Eq(InfoDecode.Human("AT+GTDUALSIM?", new List<string> { "+GTDUALSIM : 0, \"SUB1\", \"NO SERVICE\"" }), "Dual-SIM disabled (SUB1: no service)", "dualsim");
            Eq(InfoDecode.Human("AT+GTTXPOWER?", new List<string> { "+GTTXPOWER: -127, 255, 0", "+GTTXPOWER: -127, 255, 1" }), "Not transmitting", "TXP сентинелы");
            Eq(InfoDecode.Human("AT+EGMR=0,10", new List<string> { "+EGMR: \"352455106006264\"" }), "352455106006264", "EGMR → eSIM IMEI (цифры из префикс-строки)");
            Eq(InfoDecode.Human("AT+CFSN", new List<string> { "+CFSN: \"F350GL0012345678\"" }), "F350GL0012345678", "CFSN → серийник");
            Eq(InfoDecode.Human("AT+CFSN", new List<string> { "+CFSN: \"\"" }), "Empty", "CFSN пустой → Empty");

            Eq(InfoDecode.Human("AT+GTBASELINEVER?", new List<string> { "+GTBASELINEVER: \"gem-mp-1907-mp1 ", "_gem-mp-1907-mp1.V1.30.1\"", "+GTBASELINEVER: \"MOLY.NR15.R3.MD700.TC35.SP.V110.5.P16\"" }),
                "V1.30.1 (P16)", "baseline compact versions");

            Eq(InfoDecode.Human("ATI", new List<string> { "Manufacturer: Fibocom Wireless Inc.", "Model: FM350-GL", "Revision: 81600.0000.00.29.24.02", "SVN: 10", "IMEI: 352455106006257" }),
                "Fibocom FM350-GL, firmware 81600.0000.00.29.24.02", "ATI compact model without identity fields");

            Console.WriteLine();
            Console.WriteLine("PASS: " + _pass + "  FAIL: " + _fail);
            return _fail == 0 ? 0 : 1;
        }
    }
}
