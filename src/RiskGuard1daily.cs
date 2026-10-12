// RiskGuard by 1daily.ai — version 2.4
// ------------------------------------------------------------------------------------------
// Account-wide risk protection. Runs on one chart (XAUUSD recommended) and watches ALL
// positions of the account, no matter where they were opened (mobile, web, desktop, other
// cBots). Reactive: violations are closed ~0.5 s after execution.
//
// Rules:
//   1. Lot size        fixed max lots, or max lots = equity x factor / (contract size x price)
//   2. Risk per trade  loss at stop loss <= x % of balance; missing or too wide SL is set to the
//                      max-risk distance (or the position is closed, selectable)
//   3. Daily risk      equity loss since daily reset >= limit -> close all, lock until daily reset
//   4. Weekly risk     equity loss since weekly reset >= limit -> close all, lock until weekly reset
//   5. Hedge block     buy + sell on the same symbol -> the newer position is closed
//   6. Trades per day  further positions are closed immediately
//
// Display: status panel (top left) and lock banner (chart centre). Visible when the instance
//          runs locally in cTrader Windows/Mac. Cloud instances have no chart.
//
// Restart: locks survive a restart. On start the cBot rebuilds them from the account history
//          and explains in the log why the account is locked.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum DisplayTimeZone { Platform, UTC, CentralEurope, London, NewYork }
    public enum LotLimitMode { Fixed, EquityFactor, Off }
    public enum RiskAction { SetStopLoss, ClosePosition }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None, AddIndicators = false)]
    public class RiskGuard1daily : Robot
    {
        // ================================================================== Parameters

        [Parameter("Mode", Group = "1 Lot size", DefaultValue = LotLimitMode.Off)]
        public LotLimitMode LotMode { get; set; }

        [Parameter("Max lots (mode Fixed)", Group = "1 Lot size", DefaultValue = 0.10, MinValue = 0.01, Step = 0.01)]
        public double MaxLotsFixed { get; set; }

        [Parameter("Max exposure x equity (mode EquityFactor)", Group = "1 Lot size", DefaultValue = 1.5, MinValue = 0.1, Step = 0.1)]
        public double ExposureFactor { get; set; }

        [Parameter("Minimum lot always allowed (mode EquityFactor)", Group = "1 Lot size", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double MinLot { get; set; }

        [Parameter("Max risk per trade % of balance (0 = off)", Group = "2 Risk per trade", DefaultValue = 0.0, MinValue = 0, Step = 0.1)]
        public double RiskPerTradePct { get; set; }

        [Parameter("Missing or too wide stop loss", Group = "2 Risk per trade", DefaultValue = RiskAction.SetStopLoss)]
        public RiskAction RiskViolationAction { get; set; }

        [Parameter("Seconds to add a stop loss after opening", Group = "2 Risk per trade", DefaultValue = 10, MinValue = 0, MaxValue = 300)]
        public int SlGraceSeconds { get; set; }

        [Parameter("Max daily loss % (0 = off)", Group = "3 Daily risk", DefaultValue = 3.0, MinValue = 0, Step = 0.1)]
        public double DailyLossPct { get; set; }

        [Parameter("Daily reset hour (New York time)", Group = "3 Daily risk", DefaultValue = 17, MinValue = 0, MaxValue = 23)]
        public int DayResetHourNy { get; set; }

        [Parameter("Max weekly loss % (0 = off)", Group = "4 Weekly risk", DefaultValue = 0.0, MinValue = 0, Step = 0.1)]
        public double WeeklyLossPct { get; set; }

        [Parameter("Weekly reset hour, Sunday (New York time)", Group = "4 Weekly risk", DefaultValue = 17, MinValue = 0, MaxValue = 23)]
        public int WeekResetHourNy { get; set; }

        [Parameter("Block opposite positions", Group = "5 Hedge block", DefaultValue = false)]
        public bool HedgeBlock { get; set; }

        [Parameter("Max trades per day (0 = off)", Group = "6 Trades per day", DefaultValue = 0, MinValue = 0)]
        public int TradesPerDay { get; set; }

        [Parameter("Show status panel", Group = "7 Display", DefaultValue = true)]
        public bool ShowPanel { get; set; }

        [Parameter("Show lock banner", Group = "7 Display", DefaultValue = true)]
        public bool ShowBanner { get; set; }

        [Parameter("Time zone for panel and log", Group = "7 Display", DefaultValue = DisplayTimeZone.Platform)]
        public DisplayTimeZone DisplayTz { get; set; }

        // ================================================================== State

        private const string Version = "2.4";          // increase with every change; shown in log and panel
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private const string OffMarker = "RiskGuard1daily_OFF";
        private const double HistoryTolerance = 0.97;   // forced closes can fill slightly better than the trigger

        private DateTime _dayStart, _weekStart;
        private double _dayStartBal, _weekStartBal;

        private string _lockType;            // null | daily | weekly
        private DateTime _lockUntil;
        private string _lockReason = "";     // short reason for panel and banner

        private readonly HashSet<int> _closing = new HashSet<int>();
        private readonly HashSet<int> _checked = new HashSet<int>();
        private DateTime _lastSweep = DateTime.MinValue;
        private DateTime _startedAt;

        private string Cur => Account.Asset.Name;

        // ================================================================== Equity & quote subscriptions
        // In the cloud the instance may only receive quotes for its own chart symbol. Every symbol
        // with an open position is therefore subscribed explicitly, and equity is cross-checked
        // against balance + open P&L of all positions. The lower (more conservative) value counts.

        private readonly HashSet<string> _watched = new HashSet<string>();
        private DateTime _lastHeartbeat = DateTime.MinValue;
        private DateTime _lastEquityNote = DateTime.MinValue;
        private int _warnedLevel;

        private double EquityFromPositions() => Account.Balance + Positions.Sum(p => p.NetProfit);

        private double EquityNow() => Math.Min(Account.Equity, EquityFromPositions());

        private void WatchSymbol(string symbolName)
        {
            if (string.IsNullOrEmpty(symbolName) || _watched.Contains(symbolName)) return;
            var s = Symbols.GetSymbol(symbolName);
            if (s == null) return;
            s.Tick += _ => Safe("tick " + symbolName, EvaluateEquity);
            _watched.Add(symbolName);
            Log($"Watching live quotes of {symbolName}");
        }

        private void WatchAllPositionSymbols()
        {
            foreach (var p in Positions) WatchSymbol(p.SymbolName);
        }

        private void Diagnostics()
        {
            var now = Server.TimeInUtc;
            double acc = Account.Equity, calc = EquityFromPositions();
            double day = Math.Min(acc, calc) - _dayStartBal;

            // big difference between account equity and position P&L -> log (max. every 10 min)
            if (Math.Abs(acc - calc) > Math.Max(1, Account.Balance * 0.002) && (now - _lastEquityNote).TotalMinutes >= 10)
            {
                _lastEquityNote = now;
                Log($"NOTE: account equity {Money(acc)} differs from balance + open P&L {Money(calc)} — RiskGuard uses the lower value {Money(Math.Min(acc, calc))}");
            }

            // warnings at 50 % and 80 % of the daily limit
            if (DailyLossPct > 0 && _dayStartBal > 0)
            {
                double used = Math.Max(0, -day) / DayLimitUsd;
                int lvl = used >= 0.8 ? 80 : used >= 0.5 ? 50 : 0;
                if (lvl > _warnedLevel)
                {
                    _warnedLevel = lvl;
                    Log($"WARNING: {lvl} % of the daily loss limit used · P&L today {SMoney(day)} {Cur} · lock at equity {Money(_dayStartBal - DayLimitUsd)}");
                }
            }

            // heartbeat every 15 minutes: proves in the log that the guard is alive and what it sees
            if ((now - _lastHeartbeat).TotalMinutes >= 15)
            {
                _lastHeartbeat = now;
                Log($"Heartbeat v{Version} · account #{Account.Number} · equity {Money(acc)} (account) / {Money(calc)} (positions) · " +
                    $"P&L today {SMoney(day)} {Cur} ({Pct(day, _dayStartBal)})" +
                    (DailyLossPct > 0 ? $" · lock at equity {Money(_dayStartBal - DayLimitUsd)}" : " · daily limit off") +
                    $" · {Positions.Count} open position(s){(Locked ? " · LOCKED" : "")}");
            }
        }

        // ================================================================== Lifecycle

        protected override void OnStart()
        {
            _startedAt = Server.TimeInUtc;
            Positions.Opened += a => Safe("position opened", () => CheckNewPosition(a.Position, atStart: false));
            Positions.Closed += a => Safe("position closed", () => OnPositionClosed(a));

            Log($"==== RiskGuard by 1daily.ai v{Version} started ====");
            Log($"Account {Account.BrokerName} #{Account.Number} · equity {Money(Account.Equity)} {Cur} · leverage 1:{Account.PreciseLeverage:F0}");
            Log("Rules: " +
                $"lot size {LotRuleText()} · " +
                $"risk/trade {(RiskPerTradePct > 0 ? Num(RiskPerTradePct) + " % (" + (RiskViolationAction == RiskAction.SetStopLoss ? "set SL" : "close") + " after " + SlGraceSeconds + " s)" : "off")} · " +
                $"daily loss {(DailyLossPct > 0 ? Num(DailyLossPct) + " %" : "off")} · " +
                $"weekly loss {(WeeklyLossPct > 0 ? Num(WeeklyLossPct) + " %" : "off")} · " +
                $"hedge block {(HedgeBlock ? "on" : "off")} · " +
                $"max trades/day {(TradesPerDay > 0 ? TradesPerDay.ToString(CI) : "off")}");
            Log($"Times are shown in {TzLabel()}.");

            try { Log($"Environment: {Application.EnvironmentType} · mode {RunningMode} · chart {SymbolName}"); } catch { }
            WatchSymbol(SymbolName);
            WatchAllPositionSymbols();

            ResetPeriods(force: true, quiet: true);
            StartupCheck();

            foreach (var p in Positions.ToList()) CheckNewPosition(p, atStart: true);
            EvaluateEquity();
            if (Locked)
            {
                CloseAll("account locked");
                CancelAllPending("account locked");
            }
            Log(Locked
                ? $"==== Start complete · ACCOUNT LOCKED until {TLong(_lockUntil)} ===="
                : "==== Start complete · no lock active, trading allowed within the rules ====");

            BuildUi();
            UpdateUi();
            Timer.Start(TimeSpan.FromSeconds(1));
        }

        protected override void OnStop()
        {
            Log("RiskGuard stopped — the account is NOT protected while the instance is not running.");
            try
            {
                // interactive objects stay on the chart after the cBot has stopped
                var t = Chart.DrawStaticText(OffMarker, "RiskGuard by 1daily.ai · OFF · account unprotected",
                    VerticalAlignment.Top, HorizontalAlignment.Left, Color.FromHex("#FFD08A1B"));
                t.IsInteractive = true;
            }
            catch { }
        }

        protected override void OnTick() => Safe("tick", EvaluateEquity);

        protected override void OnTimer() => Safe("timer", TimerWork);

        /// Runs guard logic; an unexpected error is logged but never stops the guard.
        private readonly Dictionary<string, DateTime> _lastError = new Dictionary<string, DateTime>();
        private void Safe(string where, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                var now = Server.TimeInUtc;
                string key = where + "|" + ex.Message;
                if (_lastError.TryGetValue(key, out var last) && (now - last).TotalSeconds < 60) return;   // max. once per minute
                _lastError[key] = now;
                Log($"ERROR in {where}: {ex.GetType().Name}: {ex.Message} — guard keeps running");
            }
        }

        private void TimerWork()
        {
            WatchAllPositionSymbols();
            ResetPeriods(force: false, quiet: false);
            ExpireLock();
            EvaluateEquity();
            if (!Locked) CheckRiskAll();
            Diagnostics();

            // while locked: retry positions whose close failed (e.g. market closed), every 30 s
            if (Locked && (Server.TimeInUtc - _lastSweep).TotalSeconds >= 30)
            {
                _lastSweep = Server.TimeInUtc;
                CloseAll("account locked");
            }
            UpdateUi();
        }

        // ================================================================== Periods & history

        private static DateTime U(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

        private List<HistoricalTrade> ClosedSince(DateTime t) =>
            History.Where(h => U(h.ClosingTime) >= t).OrderBy(h => h.ClosingTime).ToList();

        private double RealizedSince(DateTime t) =>
            History.Where(h => U(h.ClosingTime) >= t).Sum(h => h.NetProfit);

        private void ResetPeriods(bool force, bool quiet)
        {
            var now = Server.TimeInUtc;
            var ds = LastDayReset(now);
            var ws = LastWeekReset(now);
            if (force || ds != _dayStart)
            {
                _dayStart = ds;
                _dayStartBal = Account.Balance - RealizedSince(ds);
                _checked.Clear();
                _warnedLevel = 0;
                if (!quiet) Log($"New trading day since {TLong(ds)} · start balance {Money(_dayStartBal)} {Cur}");
            }
            if (force || ws != _weekStart)
            {
                _weekStart = ws;
                _weekStartBal = Account.Balance - RealizedSince(ws);
                if (!quiet) Log($"New trading week since {TLong(ws)} · start balance {Money(_weekStartBal)} {Cur}");
            }
        }

        private int TradesToday()
        {
            var ids = new HashSet<int>();
            foreach (var h in History) if (U(h.EntryTime) >= _dayStart) ids.Add(h.PositionId);
            foreach (var p in Positions) if (U(p.EntryTime) >= _dayStart) ids.Add(p.Id);
            return ids.Count;
        }

        // ================================================================== Startup check (explains locks from the past)

        private void StartupCheck()
        {
            var now = Server.TimeInUtc;
            double unreal = EquityNow() - Account.Balance;
            Log("---- Startup check (based on account history) ----");

            // ---------- Today
            var today = ClosedSince(_dayStart);
            double realizedDay = today.Sum(h => h.NetProfit);
            double day = EquityNow() - _dayStartBal;
            Log($"Trading day since {TLong(_dayStart)} · start balance {Money(_dayStartBal)} {Cur}");
            Log($"Today: {TradesToday()} trade(s) opened · {today.Count} close(s) · realized {SMoney(realizedDay)} · open P&L {SMoney(unreal)} · total {SMoney(day)} {Cur} ({Pct(day, _dayStartBal)})");

            if (DailyLossPct > 0 && _dayStartBal > 0)
            {
                double lim = DayLimitUsd;
                Log($"Daily loss limit: -{Money(lim)} {Cur} ({Num(DailyLossPct)} % of start balance) = lock at equity {Money(_dayStartBal - lim)} · {Used(day, lim)} used · risk left today {Money(Math.Max(0, lim + day))} {Cur} ({(Math.Max(0, lim + day) / _dayStartBal * 100).ToString("0.00", CI)} %)");

                int idx = CrossIndex(today, -lim * HistoryTolerance, out double atCross);
                if (idx >= 0)
                {
                    var hitAt = U(today[idx].ClosingTime);
                    SetLock("daily", NextDayReset(now), $"Daily loss limit reached at {T(hitAt)} (before start)");
                    Log("LOCKED: the daily loss limit was already reached earlier today.");
                    Log($"  Realized P&L reached {SMoney(atCross)} {Cur} at {TLong(hitAt)} (limit -{Money(lim)} {Cur}).");
                    Log("  Results within 3 % of the limit count as reached, because forced closes can fill slightly better than the trigger.");
                    LogTrades("  Closed trades today up to that point:", today, idx);
                    Log($"  Trading stays locked until {TLong(_lockUntil)} ({Remaining(_lockUntil - now)} from now). New positions will be closed immediately.");
                }
            }

            // ---------- This week
            var week = ClosedSince(_weekStart);
            double wk = EquityNow() - _weekStartBal;
            Log($"Trading week since {TLong(_weekStart)} · start balance {Money(_weekStartBal)} {Cur} · total {SMoney(wk)} {Cur} ({Pct(wk, _weekStartBal)})");

            if (WeeklyLossPct > 0 && _weekStartBal > 0)
            {
                double lim = WeekLimitUsd;
                Log($"Weekly loss limit: -{Money(lim)} {Cur} ({Num(WeeklyLossPct)} % of start balance) · {Used(wk, lim)} used");

                int idx = CrossIndex(week, -lim * HistoryTolerance, out double atCross);
                if (idx >= 0)
                {
                    var hitAt = U(week[idx].ClosingTime);
                    SetLock("weekly", NextWeekReset(now), $"Weekly loss limit reached on {TLong(hitAt)} (before start)");
                    Log("LOCKED: the weekly loss limit was already reached earlier this week.");
                    Log($"  Realized P&L reached {SMoney(atCross)} {Cur} on {TLong(hitAt)} (limit -{Money(lim)} {Cur}).");
                    Log("  Realized P&L this week by trading day:");
                    foreach (var g in week.Take(idx + 1).GroupBy(h => LastDayReset(U(h.ClosingTime))).OrderBy(g => g.Key))
                        Log($"    {DayName(g.Key)}  {SMoney(g.Sum(h => h.NetProfit)),12} {Cur}  ({g.Count()} close(s))");
                    Log($"  Trading stays locked until {TLong(_lockUntil)} ({Remaining(_lockUntil - now)} from now). New positions will be closed immediately.");
                }
            }

            // ---------- Trades per day
            if (TradesPerDay > 0)
            {
                int n = TradesToday();
                if (n >= TradesPerDay)
                    Log($"Trades today: {n} / {TradesPerDay} -> daily trade limit already reached. New positions will be closed until {TLong(NextDayReset(now))}.");
                else
                    Log($"Trades today: {n} / {TradesPerDay}");
            }

            Log($"Open positions: {Positions.Count}{(Positions.Count > 0 ? " (checked below)" : "")}");
        }

        /// Index of the closed trade at which the cumulative result first reached the threshold, or -1.
        private static int CrossIndex(List<HistoricalTrade> trades, double threshold, out double atCross)
        {
            double run = 0;
            for (int i = 0; i < trades.Count; i++)
            {
                run += trades[i].NetProfit;
                if (run <= threshold) { atCross = run; return i; }
            }
            atCross = run;
            return -1;
        }

        private void LogTrades(string title, List<HistoricalTrade> trades, int lastIdx)
        {
            Log(title);
            int from = Math.Max(0, lastIdx - 9);
            if (from > 0) Log($"    ... {from} earlier close(s) not listed");
            double run = trades.Take(from).Sum(h => h.NetProfit);
            for (int i = from; i <= lastIdx; i++)
            {
                var h = trades[i];
                run += h.NetProfit;
                Log($"    {T(U(h.ClosingTime))}  #{h.PositionId} {h.SymbolName} {h.TradeType} {h.Quantity:0.00} lots  {SMoney(h.NetProfit),10}  (cumulative {SMoney(run)})");
            }
        }

        // ================================================================== Locks

        private double DayLimitUsd => _dayStartBal * DailyLossPct / 100;
        private double WeekLimitUsd => _weekStartBal * WeeklyLossPct / 100;
        private bool Locked => _lockType != null && _lockUntil > Server.TimeInUtc;

        private static int Prio(string t) => t == "weekly" ? 2 : t == "daily" ? 1 : 0;

        private void EvaluateEquity()
        {
            if (_dayStartBal <= 0) return;
            var now = Server.TimeInUtc;
            double eq = EquityNow();
            double unreal = eq - Account.Balance;

            // 3. Weekly risk (takes priority over the daily lock)
            double wk = eq - _weekStartBal;
            if (WeeklyLossPct > 0 && _weekStartBal > 0 && wk <= -WeekLimitUsd && !(Locked && _lockType == "weekly"))
            {
                SetLock("weekly", NextWeekReset(now), $"Weekly loss {SMoney(wk)} {Cur} ({Pct(wk, _weekStartBal)}) at {T(now)}");
                Log($"WEEKLY LOSS LIMIT HIT: P&L this week {SMoney(wk)} {Cur} ({Pct(wk, _weekStartBal)}), incl. open P&L {SMoney(unreal)}, " +
                    $"reached the limit of -{Money(WeekLimitUsd)} {Cur} ({Num(WeeklyLossPct)} %). Closing {Positions.Count} position(s). Locked until {TLong(_lockUntil)}.");
                CloseAll("weekly loss limit");
                CancelAllPending("weekly loss limit");
                return;
            }

            // 2. Daily risk
            double day = eq - _dayStartBal;
            if (DailyLossPct > 0 && day <= -DayLimitUsd && !Locked)
            {
                SetLock("daily", NextDayReset(now), $"Daily loss {SMoney(day)} {Cur} ({Pct(day, _dayStartBal)}) at {T(now)}");
                Log($"DAILY LOSS LIMIT HIT: P&L today {SMoney(day)} {Cur} ({Pct(day, _dayStartBal)}), incl. open P&L {SMoney(unreal)}, " +
                    $"reached the limit of -{Money(DayLimitUsd)} {Cur} ({Num(DailyLossPct)} %). Closing {Positions.Count} position(s). Locked until {TLong(_lockUntil)}.");
                CloseAll("daily loss limit");
                CancelAllPending("daily loss limit");
            }
        }

        private void SetLock(string type, DateTime until, string reason)
        {
            if (Locked && Prio(_lockType) > Prio(type)) return;
            if (Locked && _lockType == type && _lockUntil >= until) return;
            _lockType = type;
            _lockUntil = until;
            _lockReason = reason;
        }

        private void ExpireLock()
        {
            if (_lockType == null || _lockUntil > Server.TimeInUtc) return;
            Log($"Lock expired ({LockName(_lockType)}) — trading allowed again.");
            _lockType = null;
            _lockReason = "";
        }

        private static string LockName(string t) => t == "weekly" ? "weekly loss limit" : t == "daily" ? "daily loss limit" : "-";

        // ================================================================== Checking every new position

        private double MaxLots(Symbol s, double price)
        {
            if (LotMode == LotLimitMode.Off) return double.MaxValue;
            if (LotMode == LotLimitMode.Fixed) return MaxLotsFixed;
            if (price <= 0) return double.MaxValue;
            double cap = EquityNow() * ExposureFactor / (s.LotSize * price);
            cap = Math.Max(cap, MinLot);
            return Math.Floor(cap * 100 + 1e-9) / 100;
        }

        private string LotRuleText() =>
            LotMode == LotLimitMode.Fixed ? "max " + MaxLotsFixed.ToString("0.00", CI) + " lots (fixed)"
            : LotMode == LotLimitMode.EquityFactor ? Num(ExposureFactor) + " x equity"
            : "off";

        private string ExplainCap(Symbol s, double price, double cap)
        {
            if (LotMode == LotLimitMode.Fixed) return $"fixed max of {cap.ToString("0.00", CI)} lots";
            return $"max {cap.ToString("0.00", CI)} lots = {Num(ExposureFactor)} x equity {Money(EquityNow())} / ({s.LotSize.ToString("0.##", CI)} x {price.ToString("0.#####", CI)})";
        }

        // ================================================================== Risk per trade

        private double MaxRiskMoney => Account.Balance * RiskPerTradePct / 100;

        /// Loss in account currency if the stop loss is hit; infinity without stop loss.
        private static double RiskMoney(Position p, Symbol s, double? sl)
        {
            if (sl == null) return double.PositiveInfinity;
            double dist = p.TradeType == TradeType.Buy ? p.EntryPrice - sl.Value : sl.Value - p.EntryPrice;
            if (dist <= 0) return 0;   // stop loss at break-even or in profit
            return dist / s.PipSize * s.PipValue * p.VolumeInUnits;
        }

        /// Largest stop-loss distance (price units) that keeps the loss within maxRisk.
        private static double MaxSlDistance(Symbol s, double volumeInUnits, double maxRisk) =>
            volumeInUnits > 0 && s.PipValue > 0 ? maxRisk / (s.PipValue * volumeInUnits) * s.PipSize : 0;

        private void CheckRiskAll()
        {
            if (RiskPerTradePct <= 0) return;
            var now = Server.TimeInUtc;
            foreach (var p in Positions.ToList())
            {
                if (_closing.Contains(p.Id)) continue;
                if ((now - U(p.EntryTime)).TotalSeconds < SlGraceSeconds) continue;
                var pos = p;
                Safe("risk check #" + pos.Id, () => CheckRisk(pos));
            }
        }

        private void CheckRisk(Position p)
        {
            var s = Symbols.GetSymbol(p.SymbolName);
            if (!s.MarketHours.IsOpened()) return;           // nothing can be changed while the market is closed
            double maxRisk = MaxRiskMoney;
            double risk = RiskMoney(p, s, p.StopLoss);
            if (risk <= maxRisk * 1.001) return;

            string why = p.StopLoss == null
                ? $"no stop loss (max risk {Num(RiskPerTradePct)} % = {Money(maxRisk)} {Cur})"
                : $"risk at stop loss {Money(risk)} {Cur} ({Pct(-risk, Account.Balance)}) exceeds max {Num(RiskPerTradePct)} % = {Money(maxRisk)} {Cur}";

            if (RiskViolationAction == RiskAction.ClosePosition)
            {
                Close(p, why);
                return;
            }

            double dist = MaxSlDistance(s, p.VolumeInUnits, maxRisk);
            double tick = s.TickSize > 0 ? s.TickSize : s.PipSize;
            double target = p.TradeType == TradeType.Buy
                ? Math.Ceiling((p.EntryPrice - dist) / tick - 1e-9) * tick     // round towards entry = smaller risk
                : Math.Floor((p.EntryPrice + dist) / tick + 1e-9) * tick;
            target = Math.Round(target, s.Digits);

            bool beyond = p.TradeType == TradeType.Buy ? s.Bid <= target : s.Ask >= target;
            if (dist <= 0 || beyond)
            {
                Close(p, why + " — price is already beyond the max-risk stop loss");
                return;
            }

            var r = p.ModifyStopLossPrice(target);
            if (r.IsSuccessful)
                Log($"STOP LOSS SET #{p.Id} {p.TradeType} {p.Quantity:0.00} {p.SymbolName}: {why} -> SL {target.ToString("0.#####", CI)} (distance {dist.ToString("0.#####", CI)}, risk {Money(maxRisk)} {Cur})");
            else
                Close(p, why + $" — setting the stop loss failed ({r.Error})");
        }

        private string RiskInfo(Position p)
        {
            if (RiskPerTradePct <= 0) return "";
            var s = Symbols.GetSymbol(p.SymbolName);
            double risk = RiskMoney(p, s, p.StopLoss);
            if (double.IsInfinity(risk))
                return $" · no SL yet: {(RiskViolationAction == RiskAction.SetStopLoss ? "SL will be set" : "position will be closed")} after {SlGraceSeconds} s";
            return $" · risk {Money(risk)} {Cur} ({Pct(-risk, Account.Balance)})";
        }

        private void CheckNewPosition(Position p, bool atStart)
        {
            WatchSymbol(p.SymbolName);
            if (_closing.Contains(p.Id) || _checked.Contains(p.Id)) return;
            _checked.Add(p.Id);
            string desc = $"#{p.Id} {p.TradeType} {p.Quantity:0.00} {p.SymbolName}";

            // Lock
            if (Locked)
            {
                Close(p, $"account locked ({LockName(_lockType)}) until {TLong(_lockUntil)}");
                return;
            }

            // 1. Lot size
            var s = Symbols.GetSymbol(p.SymbolName);
            double price = p.TradeType == TradeType.Buy ? s.Ask : s.Bid;
            double cap = MaxLots(s, price);
            if (p.Quantity > cap + 1e-9)
            {
                Close(p, $"{p.Quantity:0.00} lots exceeds the {ExplainCap(s, price, cap)}");
                return;
            }

            // 4. Hedge block
            if (HedgeBlock)
            {
                var opp = Positions.FirstOrDefault(o => o.Id != p.Id && !_closing.Contains(o.Id)
                                                        && o.SymbolName == p.SymbolName && o.TradeType != p.TradeType);
                if (opp != null)
                {
                    var newer = opp.EntryTime > p.EntryTime ? opp : p;
                    var older = newer == p ? opp : p;
                    Close(newer, $"hedge block — opposite position #{older.Id} {older.TradeType} on {p.SymbolName} is open");
                    return;
                }
            }

            if (atStart)
            {
                Log($"Existing position {desc} checked · within the rules");
                return;
            }

            // 5. Trades per day
            int n = TradesToday();
            if (TradesPerDay > 0)
            {
                if (n > TradesPerDay)
                {
                    Close(p, $"trade {n} of max {TradesPerDay} today (limit resets {TLong(NextDayReset(Server.TimeInUtc))})");
                    return;
                }
                if (n == TradesPerDay) Log($"WARNING: this is the last allowed trade today ({n}/{TradesPerDay}).");
            }

            Log($"OK {desc} · max {(cap >= double.MaxValue ? "unlimited" : cap.ToString("0.00", CI))} lots · trade {n}{(TradesPerDay > 0 ? "/" + TradesPerDay : "")} today{RiskInfo(p)} · P&L today {SMoney(EquityNow() - _dayStartBal)} {Cur}");
        }

        private void OnPositionClosed(PositionClosedEventArgs a)
        {
            var p = a.Position;
            _closing.Remove(p.Id);
            _checked.Remove(p.Id);
            Log($"#{p.Id} {p.SymbolName} closed · {SMoney(p.NetProfit)} · P&L today {SMoney(EquityNow() - _dayStartBal)} {Cur}");
            EvaluateEquity();
        }

        // ================================================================== Actions

        private void Close(Position p, string reason)
        {
            if (_closing.Contains(p.Id)) return;
            _closing.Add(p.Id);
            Log($"CLOSING #{p.Id} {p.TradeType} {p.Quantity:0.00} {p.SymbolName}: {reason}");
            ClosePositionAsync(p, r => Safe("close callback", () =>
            {
                if (!r.IsSuccessful)
                {
                    _closing.Remove(p.Id);
                    _checked.Remove(p.Id);
                    Log($"Close #{p.Id} failed: {r.Error} — will retry");
                }
            }));
        }

        private void CloseAll(string reason)
        {
            foreach (var p in Positions.ToList()) Close(p, reason);
        }

        private void CancelAllPending(string reason)
        {
            var orders = PendingOrders.ToList();
            foreach (var o in orders) CancelPendingOrderAsync(o);
            if (orders.Count > 0) Log($"{orders.Count} pending order(s) cancelled: {reason}");
        }

        // ================================================================== Display: status panel + lock banner

        private static readonly Color CPanel = Color.FromHex("#EE161B22");
        private static readonly Color CLabel = Color.FromHex("#FFA9B4C2");
        private static readonly Color CValue = Color.FromHex("#FFF2F4F7");
        private static readonly Color CGreen = Color.FromHex("#FF1F9D55");
        private static readonly Color CRed = Color.FromHex("#FFD33A3A");
        private static readonly Color CPosText = Color.FromHex("#FF4ADE80");
        private static readonly Color CWarnText = Color.FromHex("#FFF5B942");
        private static readonly Color CBadText = Color.FromHex("#FFFF6B6B");

        private bool _ui;
        private Border _panel, _header, _banner;
        private TextBlock _hTitle, _hSub, _hReason, _lMaxLot, _vMaxLot, _vRisk, _vRiskHint, _vDayLimit, _vDay, _vLeft, _vBar, _vWeek, _vTrades, _bTitle, _bSub;

        private void BuildUi()
        {
            try
            {
                Chart.RemoveObject(OffMarker);

                _hTitle = new TextBlock { FontSize = 13, FontWeight = FontWeight.Bold, ForegroundColor = Color.White };
                _hSub = new TextBlock { FontSize = 11, ForegroundColor = Color.FromHex("#E6FFFFFF"), Margin = new Thickness(0, 1, 0, 0) };
                _hReason = new TextBlock { FontSize = 11, ForegroundColor = Color.White, Margin = new Thickness(0, 2, 0, 0), IsVisible = false };
                var hs = new StackPanel { Orientation = Orientation.Vertical };
                hs.AddChild(_hTitle);
                hs.AddChild(_hSub);
                hs.AddChild(_hReason);
                _header = new Border { Child = hs, Padding = new Thickness(10, 6, 10, 6), BackgroundColor = CGreen };

                _vMaxLot = Val(15, true);
                _vRisk = Val(12, false);
                _vRiskHint = new TextBlock { FontSize = 11, ForegroundColor = CLabel, Margin = new Thickness(0, 1, 0, 2) };
                _vDayLimit = Val(12, false);
                _vDay = Val(12, true);
                _vLeft = Val(15, true);
                _vBar = new TextBlock { FontSize = 11, ForegroundColor = CLabel, Margin = new Thickness(0, 2, 0, 4) };
                _vWeek = Val(12, false);
                _vTrades = Val(12, false);

                var body = new StackPanel { Orientation = Orientation.Vertical };
                body.AddChild(Row("Max. lot", _vMaxLot, out _lMaxLot));
                body.AddChild(Row("Risk / trade", _vRisk, out _));
                body.AddChild(_vRiskHint);
                body.AddChild(Row("Daily limit", _vDayLimit, out _));
                body.AddChild(Row("Today", _vDay, out _));
                body.AddChild(Row("Risk left today", _vLeft, out _));
                body.AddChild(_vBar);
                body.AddChild(Row("This week", _vWeek, out _));
                body.AddChild(Row("Trades today", _vTrades, out _));

                var outer = new StackPanel { Orientation = Orientation.Vertical };
                outer.AddChild(_header);
                outer.AddChild(new Border { Child = body, Padding = new Thickness(10, 6, 10, 8) });

                _panel = new Border
                {
                    Child = outer,
                    BackgroundColor = CPanel,
                    BorderColor = CGreen,
                    BorderThickness = new Thickness(1.5),
                    Width = 330,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(12, 12, 0, 0),
                    IsVisible = ShowPanel
                };
                Chart.AddControl(_panel);

                _bTitle = new TextBlock { FontSize = 26, FontWeight = FontWeight.Bold, ForegroundColor = Color.White, HorizontalAlignment = HorizontalAlignment.Center };
                _bSub = new TextBlock { FontSize = 14, ForegroundColor = Color.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                var bs = new StackPanel { Orientation = Orientation.Vertical };
                bs.AddChild(_bTitle);
                bs.AddChild(_bSub);
                _banner = new Border
                {
                    Child = bs,
                    BackgroundColor = Color.FromHex("#D9B91C1C"),
                    Padding = new Thickness(32, 16, 32, 16),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsVisible = false
                };
                Chart.AddControl(_banner);
                _ui = true;
            }
            catch (Exception ex)
            {
                _ui = false;   // e.g. cloud: no chart -> keep protecting without display
                Log("Display not available: " + ex.Message);
            }
        }

        private static TextBlock Val(int size, bool bold)
        {
            var t = new TextBlock { FontSize = size, ForegroundColor = CValue };
            if (bold) t.FontWeight = FontWeight.Bold;
            return t;
        }

        private static StackPanel Row(string label, TextBlock value, out TextBlock labelBlock)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            labelBlock = new TextBlock { Text = label, Width = 105, FontSize = 12, ForegroundColor = CLabel, VerticalAlignment = VerticalAlignment.Center };
            value.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(labelBlock);
            row.AddChild(value);
            return row;
        }

        private void UpdateUi()
        {
            if (!_ui) return;
            try
            {
                var now = Server.TimeInUtc;
                double eq = EquityNow();
                bool locked = Locked;

                // Header
                var frame = locked ? CRed : CGreen;
                _header.BackgroundColor = frame;
                _panel.BorderColor = frame;
                if (locked)
                {
                    _hTitle.Text = "RiskGuard by 1daily.ai";
                    _hSub.Text = $"LOCKED · {LockName(_lockType)} · until {T(_lockUntil)} ({Remaining(_lockUntil - now)})";
                    _hReason.Text = _lockReason;
                    _hReason.IsVisible = true;
                }
                else
                {
                    _hTitle.Text = "RiskGuard by 1daily.ai";
                    _hSub.Text = $"ACTIVE · v{Version} · since {TLong(_startedAt)} · daily reset {T(NextDayReset(now))}";
                    _hReason.IsVisible = false;
                }

                // Max. lot
                _lMaxLot.Text = "Max. lot " + Symbol.Name;
                if (locked) { _vMaxLot.Text = "0 · locked"; _vMaxLot.ForegroundColor = CBadText; }
                else
                {
                    double cap = MaxLots(Symbol, Symbol.Ask);
                    _vMaxLot.Text = cap >= double.MaxValue ? "unlimited" : cap.ToString("0.00", CI) + (LotMode == LotLimitMode.Fixed ? "  (fixed)" : "");
                    _vMaxLot.ForegroundColor = CValue;
                }

                // Risk per trade
                if (RiskPerTradePct > 0)
                {
                    double maxRisk = MaxRiskMoney;
                    _vRisk.Text = $"{Num(RiskPerTradePct)} % = {Money(maxRisk)} {Cur}";
                    double capLots = MaxLots(Symbol, Symbol.Ask);
                    if (capLots < double.MaxValue && !locked)
                    {
                        double dist = MaxSlDistance(Symbol, Symbol.QuantityToVolumeInUnits(capLots), maxRisk);
                        _vRiskHint.Text = $"at {capLots.ToString("0.00", CI)} lots: stop loss max {dist.ToString("0.00", CI)} from entry";
                    }
                    else _vRiskHint.Text = "";
                }
                else
                {
                    _vRisk.Text = "off";
                    _vRiskHint.Text = "";
                }

                // Today
                double day = eq - _dayStartBal;
                if (DailyLossPct > 0 && _dayStartBal > 0)
                {
                    double lim = DayLimitUsd;
                    double used = Math.Max(0, -day) / lim;
                    _vDayLimit.Text = $"-{Money(lim)} {Cur}  (-{Num(DailyLossPct)} %)";
                    var c = day >= 0 ? CPosText : used >= 0.8 ? CBadText : used >= 0.5 ? CWarnText : CValue;
                    _vDay.Text = $"{SMoney(day)} {Cur}  ({Pct(day, _dayStartBal)})";
                    _vDay.ForegroundColor = day >= 0 ? CPosText : c;

                    // profit made today adds to the room: the lock level is fixed at start balance - limit
                    double left = Math.Max(0, lim + day);
                    _vLeft.Text = $"{Money(left)} {Cur}  ({(left / _dayStartBal * 100).ToString("0.00", CI)} %)";
                    _vLeft.ForegroundColor = c;

                    int filled = (int)Math.Round(Math.Min(1, used) * 12);
                    _vBar.Text = new string('█', filled) + new string('░', 12 - filled) +
                                 $"  {(used * 100).ToString("0", CI)} % used · lock at equity {Money(_dayStartBal - lim)}";
                    _vBar.ForegroundColor = used >= 0.5 ? c : CLabel;
                }
                else
                {
                    _vDayLimit.Text = "off";
                    _vDay.Text = _dayStartBal > 0 ? $"{SMoney(day)} {Cur}  ({Pct(day, _dayStartBal)})" : "-";
                    _vDay.ForegroundColor = day >= 0 ? CPosText : CValue;
                    _vLeft.Text = "no daily limit";
                    _vLeft.ForegroundColor = CValue;
                    _vBar.Text = "";
                }

                // This week
                double wk = eq - _weekStartBal;
                if (WeeklyLossPct > 0 && _weekStartBal > 0)
                {
                    double used = Math.Max(0, -wk) / WeekLimitUsd;
                    _vWeek.Text = $"{SMoney(wk)} / -{Money(WeekLimitUsd)} {Cur}";
                    _vWeek.ForegroundColor = wk >= 0 ? CPosText : used >= 0.8 ? CBadText : used >= 0.5 ? CWarnText : CValue;
                }
                else
                {
                    _vWeek.Text = $"{SMoney(wk)} {Cur} · limit off";
                    _vWeek.ForegroundColor = CValue;
                }

                // Trades
                int n = TradesToday();
                _vTrades.Text = TradesPerDay > 0 ? $"{n} / {TradesPerDay}" : $"{n} · limit off";
                _vTrades.ForegroundColor = TradesPerDay > 0 && n >= TradesPerDay ? CWarnText : CValue;

                // Banner
                if (ShowBanner && locked)
                {
                    _bTitle.Text = $"LOCKED UNTIL {T(_lockUntil)}";
                    _bSub.Text = $"{_lockReason} · {Remaining(_lockUntil - now)} left · details in the Log";
                    _banner.IsVisible = true;
                }
                else _banner.IsVisible = false;
            }
            catch (Exception ex)
            {
                _ui = false;
                Log("Display disabled: " + ex.Message);
            }
        }

        // ================================================================== Time zones (daylight saving automatic)

        private static DateTime NthSunday(int y, int m, int n)
        {
            var d = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
            while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1);
            return d.AddDays(7 * (n - 1));
        }

        private static DateTime LastSunday(int y, int m)
        {
            var d = new DateTime(y, m, DateTime.DaysInMonth(y, m), 0, 0, 0, DateTimeKind.Utc);
            while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(-1);
            return d;
        }

        // USA: 2nd Sunday of March 07:00 UTC until 1st Sunday of November 06:00 UTC
        private static bool UsDst(DateTime utc) =>
            utc >= NthSunday(utc.Year, 3, 2).AddHours(7) && utc < NthSunday(utc.Year, 11, 1).AddHours(6);

        // Europe: last Sunday of March until last Sunday of October, 01:00 UTC
        private static bool EuDst(DateTime utc) =>
            utc >= LastSunday(utc.Year, 3).AddHours(1) && utc < LastSunday(utc.Year, 10).AddHours(1);

        private static DateTime UtcToNy(DateTime utc) => U(utc).AddHours(UsDst(utc) ? -4 : -5);
        private static DateTime NyToUtc(DateTime ny) { var g = U(ny).AddHours(4); return UsDst(g) ? g : U(ny).AddHours(5); }

        private DateTime LastDayReset(DateTime now)
        {
            var d = UtcToNy(now).Date;
            var r = NyToUtc(d.AddHours(DayResetHourNy));
            return r <= now ? r : NyToUtc(d.AddDays(-1).AddHours(DayResetHourNy));
        }

        private DateTime NextDayReset(DateTime now)
        {
            var d = UtcToNy(now).Date;
            var r = NyToUtc(d.AddHours(DayResetHourNy));
            return r > now ? r : NyToUtc(d.AddDays(1).AddHours(DayResetHourNy));
        }

        private DateTime LastWeekReset(DateTime now)
        {
            for (var d = UtcToNy(now).Date; ; d = d.AddDays(-1))
            {
                if (d.DayOfWeek != DayOfWeek.Sunday) continue;
                var r = NyToUtc(d.AddHours(WeekResetHourNy));
                if (r <= now) return r;
            }
        }

        private DateTime NextWeekReset(DateTime now)
        {
            for (var d = UtcToNy(now).Date; ; d = d.AddDays(1))
            {
                if (d.DayOfWeek != DayOfWeek.Sunday) continue;
                var r = NyToUtc(d.AddHours(WeekResetHourNy));
                if (r > now) return r;
            }
        }

        // ---------- display time

        private TimeSpan DisplayOffset(DateTime utc)
        {
            switch (DisplayTz)
            {
                case DisplayTimeZone.UTC: return TimeSpan.Zero;
                case DisplayTimeZone.CentralEurope: return TimeSpan.FromHours(EuDst(utc) ? 2 : 1);
                case DisplayTimeZone.London: return TimeSpan.FromHours(EuDst(utc) ? 1 : 0);
                case DisplayTimeZone.NewYork: return TimeSpan.FromHours(UsDst(utc) ? -4 : -5);
                default:
                    try { return Application.UserTimeOffset; } catch { return TimeSpan.Zero; }
            }
        }

        private DateTime Disp(DateTime utc) => U(utc) + DisplayOffset(utc);

        private string TzLabel()
        {
            var o = DisplayOffset(Server.TimeInUtc);
            string off = o == TimeSpan.Zero ? "UTC" : "UTC" + (o > TimeSpan.Zero ? "+" : "-") + Math.Abs(o.TotalHours).ToString("0.#", CI);
            string name = DisplayTz == DisplayTimeZone.Platform ? "cTrader platform time" : DisplayTz.ToString();
            return $"{off} ({name})";
        }

        /// "23:00" for today, otherwise "Mon 12 Oct 23:00"
        private string T(DateTime utc)
        {
            var d = Disp(utc);
            return d.Date == Disp(Server.TimeInUtc).Date ? d.ToString("HH:mm", CI) : d.ToString("ddd dd MMM HH:mm", CI);
        }

        private string TLong(DateTime utc) => Disp(utc).ToString("ddd dd MMM HH:mm", CI);
        private string DayName(DateTime dayStartUtc) => Disp(dayStartUtc.AddHours(12)).ToString("ddd dd MMM", CI);

        // ================================================================== Formatting & log

        private static string Remaining(TimeSpan t)
        {
            if (t.TotalSeconds <= 0) return "0 min";
            if (t.TotalHours >= 24) return $"{(int)t.TotalDays} d {t.Hours} h";
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, t.Minutes)} min";
        }

        private static string Num(double v) => v.ToString("0.##", CI);
        private static string Money(double v) => Math.Abs(v).ToString("N2", CI);
        private static string SMoney(double v) => (v >= 0 ? "+" : "-") + Money(v);
        private static string Pct(double v, double basis) => basis > 0 ? (v >= 0 ? "+" : "-") + Math.Abs(v / basis * 100).ToString("0.00", CI) + " %" : "-";
        private static string Used(double pnl, double limit) => limit > 0 ? (Math.Max(0, -pnl) / limit * 100).ToString("0", CI) + " %" : "-";

        private void Log(string text) => Print($"[{Disp(Server.TimeInUtc):HH:mm:ss}] {text}");
    }
}
