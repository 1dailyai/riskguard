// CloudPing — minimal cloud test for cTrader (read-only, never trades)
//
// Purpose: checks whether the cTrader Cloud can run ANY cBot from this installation/account.
//   Journal shows "CLOUDPING started"       -> cloud works, the problem is specific to RiskGuard
//   Journal shows AlgoMetadataUnavailable…  -> cloud cannot load any algo -> cTrader/cloud problem
//
// Name the cBot exactly: CloudPing   (no dot, no space)

using System;
using cAlgo.API;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.None)]
    public class CloudPing : Robot
    {
        protected override void OnStart()
        {
            Print("CLOUDPING started · account #{0} · equity {1:0.00} · open positions {2}",
                Account.Number, Account.Equity, Positions.Count);

            Positions.Opened += a => Print("CLOUDPING sees new position #{0} {1} {2:0.00} lots {3}",
                a.Position.Id, a.Position.TradeType, a.Position.Quantity, a.Position.SymbolName);

            Positions.Closed += a => Print("CLOUDPING sees closed position #{0} {1}",
                a.Position.Id, a.Position.SymbolName);

            Timer.Start(TimeSpan.FromSeconds(60));
        }

        protected override void OnTimer()
        {
            Print("CLOUDPING alive · {0:HH:mm} UTC · equity {1:0.00} · open positions {2}",
                Server.TimeInUtc, Account.Equity, Positions.Count);
        }
    }
}
