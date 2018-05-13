﻿using Akka.Actor;
using CMG.License.Services.Interfaces;
using CMG.License.Shared.DataTypes;
using System.Collections.Generic;

namespace CMG.License.UI.Actors
{
    public class LogFilesExcelProviderActor : ReceiveActor
    {
        public LogFilesExcelProviderActor(ILogFilesExcelProviderService logFilesExcelProviderService)
        {
            Receive<List<LogRptDto>>(reportRows => 
                    logFilesExcelProviderService.FillXlsxTemplate(reportRows, @"C:\Users\mgeiziry\Desktop\test.xlsx"));
        }
    }
}