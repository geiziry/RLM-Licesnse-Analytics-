﻿using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Util;
using CMG.License.Services.Interfaces;
using CMG.License.Shared.DataTypes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CMG.License.Services.Impls
{
    /// <summary>
    /// Ins and Outs to be mapped according to their server_handle
    /// Request time is same as checkout,
    /// unless the last event for this product is deny,
    /// if multiple denys, then the first deny is the request time
    /// <para Product_Last_Out/>
    /// <para Product_Last_Deny/>
    /// for checkouts without checkin in same file
    /// get files with start after checkout time
    /// limit files with first one with shutdown
    /// check for checkin in those files
    /// if not found checkin equals to the limiting shutdown
    /// </summary>
    public class LogFileRptGeneratorService : ILogFileRptGeneratorService
    {
        private ConcurrentDictionary<string, LogRptDto> InUseCheckOuts;
        private List<LogRptDto> report;

        private readonly IActorRefFactory actorSystem;

        public LogFileRptGeneratorService(IActorRefFactory actorSystem)
        {
            this.actorSystem = actorSystem;
        }

        public void InitializeReport()
        {
            InUseCheckOuts = new ConcurrentDictionary<string, LogRptDto>();
            report = new List<LogRptDto>();
        }

        public async Task<ConcurrentSet<LogRptDto>> GenerateReport(LogFile logFile)
        {
            var source = Source.From(logFile.CheckOuts);
            var getLogRptDto = Flow.Create<CheckOutDto, bool>().SelectAsyncUnordered(int.MaxValue, checkOut => GetLogRptDto(checkOut, logFile));
            var withoutCheckin = new ConcurrentSet<LogRptDto>();
            await source
                .Via(getLogRptDto)
                .RunWith(Sink.ForEachParallel<LogRptDto>(20, l =>
                {
                    if (!string.IsNullOrEmpty(l.ServerHandle))
                        withoutCheckin.TryAdd(l);
                }), actorSystem.Materializer());
            return withoutCheckin;
        }

        private Task<LogRptDto> GetLogRptDto(CheckOutDto checkOut, LogFile logFile)
        {
            return Task.Run(() =>
             {
                 //logFile.ProgressInt =  100d / logFile.CheckOuts.Count;
                 var logRptDto = new LogRptDto
                 {
                     Product = checkOut.Product,
                     Version = checkOut.Version,
                     UserName = checkOut.User,
                     HostName = checkOut.Host,
                     ServerHandle = checkOut.ServerHandle,
                     InstalledCount = GetProductInstalledLicCount(checkOut.Product, logFile.Products),
                     InUse = checkOut.CurrentInUse,
                     OutTime = checkOut.TimeStamp,
                     InTime = CheckInTimeProcessingService.GetCheckInTime(checkOut, logFile.CheckIns),
                     RequestTime = RequestTimeProcessingService.GetStrRequestTime(checkOut, logFile.Denys, logFile.CheckOuts)
                 };
                 if (logRptDto.InTime > DateTime.MinValue)
                 {
                     report.Add(logRptDto);
                     return new LogRptDto();
                 }
                 else
                     return logRptDto;
             });
        }

        public Task<bool> GetCheckInforInUseOuts(LogRptDto logRptDto, IEnumerable<LogFile> logFiles)
        {
            IOrderedEnumerable<LogFile> logFilesToCheck = from l in logFiles
                                                          where l.StartEvent.TimeStamp >= logRptDto.OutTime
                                                          orderby l.StartEvent.TimeStamp
                                                          select l;

            if (logFilesToCheck.Any())
            {
                var firstLogFileWithShutdown = (from l in logFiles
                                                where l.Shutdowns.Any()
                                                && l.StartEvent.TimeStamp >= logRptDto.OutTime
                                                orderby l.StartEvent.TimeStamp
                                                select l).FirstOrDefault();

                if (firstLogFileWithShutdown != null)
                {
                    logFilesToCheck = from l in logFilesToCheck
                                      where l.StartEvent.TimeStamp <= firstLogFileWithShutdown.StartEvent.TimeStamp
                                      orderby l.StartEvent.TimeStamp
                                      select l;

                    if (logFilesToCheck.Any())
                    {
                        foreach (var logFile in logFilesToCheck)
                        {
                            var InTime = CheckInTimeProcessingService.GetCheckInTime(logRptDto, logFile);
                            if (SetCheckInTime(InTime, logRptDto))
                                return Task.Run(() => true);
                        }

                        var firstShutdown = firstLogFileWithShutdown.Shutdowns.FirstOrDefault();
                        if (SetCheckInTime(firstShutdown.TimeStamp, logRptDto))
                            return Task.Run(() => true);
                    }
                }
                else
                {
                    foreach (var logFile in logFilesToCheck)
                    {
                        var InTime = CheckInTimeProcessingService.GetCheckInTime(logRptDto, logFile);
                        if (SetCheckInTime(InTime, logRptDto))
                            return Task.Run(() => true);
                    }
                }
            }

            return Task.Run(() => false);
        }

        private bool SetCheckInTime(DateTime InTime, LogRptDto logRptDto)
        {
            if (InTime != default(DateTime))
            {
                logRptDto.InTime = InTime;
                report.Add(logRptDto);
                return true;
            }
            return false;
        }

        public List<LogRptDto> GetReportRows()
        {
            return report;
        }

        private int GetProductInstalledLicCount(string productName, ConcurrentSet<ProductDto> products)
        {
            int licCount = 0;
            if (products.Count > 0)
            {
                var product = products.FirstOrDefault(x => x.Name == productName);
                licCount = product.InstalledCount;
            }
            return licCount;
        }
    }
}