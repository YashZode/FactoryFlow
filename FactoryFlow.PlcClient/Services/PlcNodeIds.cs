using System;
using System.Collections.Generic;
using System.Text;

namespace FactoryFlow.PlcClient.Services
{
    public static class PlcNodeIds
    {
        public const string TargetWeight = "Line1.TargetWeight";
        public const string ResetRequest = "Line1.ResetRequest";
        public const string StartBatchWithTarget = "Line1.StartBatchWithTarget";
        public const string NamespaceUri =
            "urn:factoryflow:plc:line1";

        public const string Status = "Line1.Status";
        public const string ActualWeight = "Line1.ActualWeight";
        public const string StartRequest = "Line1.StartRequest";
        public const string StopRequest = "Line1.StopRequest";
        public const string EStopActive = "Line1.EStopActive";
        public const string GuardClosed = "Line1.GuardClosed";
    }
}
