# RiskGuard by 1daily.ai

Account-wide risk protection for cTrader. One cBot, one instance per account. It watches every position on the account, no matter which device or cBot opened it (Android app, web, desktop, other cBots), and closes violations about 0.5 s after execution.

Version 2.4 · cTrader Algo API (C#) · `AccessRights.None`

## Repository contents

| Path | Content |
|---|---|
| `src/RiskGuard1daily.cs` | The cBot, full source code |
| `src/CloudPing.cs` | Minimal read-only cBot to test whether the cTrader Cloud can run an algo at all (see *Known issue*) |
| `docs/RiskGuard-Uebersicht.pdf` | Overview for developers (German): purpose, rules, parameters, implementation, open issue |

## Protection rules

| Rule | Default | Behaviour |
|---|---|---|
| Lot size | off | Mode `Fixed`: fixed maximum. Mode `EquityFactor`: `max lots = equity x factor / (contract size x price)`, the minimum lot is always allowed. A larger position is closed immediately |
| Risk per trade | off | Loss at the stop loss must not exceed the limit. After a grace period (default 10 s) a missing or too wide stop loss is set to the max-risk distance; if the price is already beyond it, the position is closed. Alternative setting: close the position instead. Checked continuously, so widening or removing the stop loss later is caught as well |
| Daily risk | 3 % | The lock level is fixed at start-of-day balance minus the limit. Equity (open positions included) at or below it → all positions closed, pending orders cancelled, account locked until the daily reset. Profit made during the day adds to the remaining risk (+1 % today leaves 4 % before the lock) and may be lost again |
| Weekly risk | off | Same, locked until the weekly reset |
| Hedge block | off | Buy and sell on the same symbol → the newer position is closed (also for positions already open when the cBot starts) |
| Trades per day | off | Further positions are closed immediately; warning in the log on the last allowed trade |

Out of the box only the daily risk rule (3 %) is active. Every other rule is switched on by setting a value (or mode/on); 0 or off disables a rule. Daily and weekly reset are set in New York time (default 17:00 = gold daily break). Daylight saving time in the US and Europe is handled in code.

## Parameters

| Group | Parameter | Default |
|---|---|---|
| 1 Lot size | Mode (`Fixed` / `EquityFactor` / `Off`) | Off |
| | Max lots (mode Fixed) | 0.10 |
| | Max exposure x equity (mode EquityFactor) | 1.5 |
| | Minimum lot always allowed (mode EquityFactor) | 0.01 |
| 2 Risk per trade | Max risk per trade % of balance (0 = off) | 0 |
| | Missing or too wide stop loss (`SetStopLoss` / `ClosePosition`) | SetStopLoss |
| | Seconds to add a stop loss after opening | 10 |
| 3 Daily risk | Max daily loss % (0 = off) | 3.0 |
| | Daily reset hour (New York time) | 17 |
| 4 Weekly risk | Max weekly loss % (0 = off) | 0 |
| | Weekly reset hour, Sunday (New York time) | 17 |
| 5 Hedge block | Block opposite positions | No |
| 6 Trades per day | Max trades per day (0 = off) | 0 |
| 7 Display | Show status panel / Show lock banner | Yes / Yes |
| | Time zone for panel and log (`Platform`, `UTC`, `CentralEurope`, `London`, `NewYork`) | Platform |

## How it works

- **New position** (`Positions.Opened`): account locked? → lot size → hedge block → trades per day. A violation closes the position and logs the reason.
- **Continuously** (`OnTick`, `Symbol.Tick` of every symbol with an open position, 1 s timer): equity = `min(Account.Equity, Balance + Σ NetProfit)`; weekly, then daily limit; stop-loss check for risk per trade; heartbeat every 15 min.
- **On start** (`OnStart`): no storage needed. Start-of-day balance, trades today and locks are rebuilt from the account history; open positions are checked; a lock is explained in the log.
- Event handlers, tick and timer work run inside a `Safe()` wrapper: an unexpected error is logged and the guard keeps running.

## Chart display

**Status panel (top left):** title, status (green active / red locked with reason and end time), max. lot for the chart symbol, risk per trade, daily limit, today's P&L, risk left today (today's profit included), progress bar with the equity level at which the lock triggers, this week, trades today.

**Lock banner (chart centre)** while the account is locked, with reason and time left.

**Stopped:** "RiskGuard by 1daily.ai · OFF · account unprotected" stays on the chart until the cBot runs again.

The display is visible when the instance runs locally in cTrader Windows/Mac. A cloud instance has no chart.

## Locked right after start? The log explains why

Locks survive a restart. On start the cBot reads the account history and checks whether the daily or weekly loss limit was already reached. If so, it locks the account and writes the reason to the log. Example (shortened):

```
[21:05:12] ==== RiskGuard by 1daily.ai v2.4 started ====
[21:05:12] Account Pepperstone #5320103 · equity 39,644.79 USD · leverage 1:200
[21:05:12] Rules: lot size off · risk/trade off · daily loss 2.5 % · weekly loss off · hedge block off · max trades/day 6
[21:05:12] ---- Startup check (based on account history) ----
[21:05:12] Trading day since Thu 08 Oct 23:00 · start balance 40,883.20 USD
[21:05:12] Today: 9 trade(s) opened · 9 close(s) · realized -1,238.41 · open P&L +0.00 · total -1,238.41 USD (-3.03 %)
[21:05:12] Daily loss limit: -1,022.08 USD (2.5 % of start balance) = lock at equity 39,861.12 · 121 % used · ...
[21:05:12] LOCKED: the daily loss limit was already reached earlier today.
[21:05:12]   Realized P&L reached -998.41 USD at Fri 09 Oct 18:09 (limit -1,022.08 USD).
[21:05:12]   Results within 3 % of the limit count as reached, because forced closes can fill slightly better than the trigger.
[21:05:12]   Closed trades today up to that point:
[21:05:12]     17:55  #247802340 XAUUSD Sell 5.00 lots     -205.00  (cumulative -205.00)
[21:05:12]     ...
[21:05:12]   Trading stays locked until Fri 09 Oct 23:00 (1 h 54 min from now). New positions will be closed immediately.
[21:05:12] Trades today: 9 / 6 -> daily trade limit already reached. New positions will be closed until Fri 09 Oct 23:00.
[21:05:12] Open positions: 0
[21:05:12] ==== Start complete · ACCOUNT LOCKED until Fri 09 Oct 23:00 ====
```

## Log lines for monitoring

The log of a cloud instance is visible in cTrader Windows/Mac/Web (Algo → instance → Log) and in the mobile app (instance → Journal).

- `==== RiskGuard by 1daily.ai v2.4 started ====` followed by the startup check: proves the code is running.
- `Heartbeat` every 15 minutes: account, equity, P&L today, lock level, open positions.
- `WARNING: 50 % / 80 % of the daily loss limit used`
- `Watching live quotes of XAUUSD`: every symbol with an open position is subscribed explicitly.
- `NOTE: account equity … differs from balance + open P&L …`: the guard uses the lower value.
- `CLOSING …`, `STOP LOSS SET …`, `DAILY LOSS LIMIT HIT …`: every action states its reason.
- `ERROR in …`: an unexpected error was caught; the guard keeps running.

## Installation

1. cTrader Windows/Mac → Algo → cBots → New → name without a dot, e.g. `RiskGuard1daily` → paste `src/RiskGuard1daily.cs` → Build.
2. **Full access off** (required for the cloud and the cTrader Store).
3. Add one instance per account, e.g. on XAUUSD m1. The chart symbol does not matter: the whole account is protected.
4. After every rebuild, stop and restart running instances; they keep running the old code otherwise.
5. Check the log: it must show `==== RiskGuard by 1daily.ai v2.4 started ====` and the startup check.

## Known issue: cTrader Cloud (status 12 Oct 2026)

Running locally in cTrader Windows, all rules work. As a **cloud instance**, the instance shows "running", but the cBot never starts: no log line of the cBot appears, and the journal shows

```
Autofac.Core.DependencyResolutionException: An exception was thrown while activating
cTrader.Automate.Arena.Realtime.RealtimeArena -> cTrader.Automate.Arena.Realtime.RealtimeArenaSettings ...
cTrader.Automate.Domain.Exceptions.AlgoMetadataUnavailableException
```

- Forum reports link this error to a dot in the cBot name (`FileNotFoundException: Could not find file '/opt/console/<name>.algo'`). A new cBot without a dot in the name still fails.
- Next steps: read the inner exception (the line containing `.algo`); run `src/CloudPing.cs` as a cloud instance. If CloudPing runs, the cause is specific to RiskGuard; if it fails the same way, the cloud cannot load any algo from this setup.
- Workaround until solved: run the instance locally, or on a VPS with the official cTrader CLI (Docker image `ghcr.io/spotware/ctrader-console`).

## Test on a demo account

1. Start → the log shows the startup check. Panel green.
2. Open a position above "Max. lot" → closed, log `CLOSING … exceeds …`.
3. Open 0.10 lots without stop loss → after 10 s a stop loss at the max risk is set, log `STOP LOSS SET`. Widen it manually → it is tightened again.
4. Buy and sell XAUUSD → the newer one is closed.
5. Max trades per day = today's trades + 1 → next trade OK, the one after closed.
6. Max daily loss slightly above today's loss → let a position run into loss → `DAILY LOSS LIMIT HIT`, everything closed, panel red, banner.
7. Stop → "OFF · account unprotected" on the chart. Start → lock is active again, the log explains why.

## Limits

- A stopped instance does not protect the account.
- Reactive: spread and slippage during the reaction window are the cost of a violation.
- The equity-factor lot formula is exact for XAUUSD and USD-quoted symbols, an approximation otherwise. Risk per trade uses the broker's pip value and is exact for all symbols.
- Risk per trade is based on the stop loss. Gaps and slippage can make the real loss larger.
- Deposits and withdrawals on the same day shift the start balance.

---

© 2026 1daily AG. All rights reserved.
