﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Alphas.Analysis;
using QuantConnect.Algorithm.Framework.Alphas.Analysis.Providers;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Alpha;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace QuantConnect.Lean.Engine.Alphas
{
    /// <summary>
    /// Default alpha handler that supports sending alphas to the messaging handler, analyzing alphas online
    /// </summary>
    public class DefaultAlphaHandler : IAlphaHandler
    {
        private const int BacktestChartSamples = 1000;
        private static readonly IReadOnlyCollection<AlphaScoreType> ScoreTypes = AlphaManager.ScoreTypes;

        private DateTime _nextMessagingUpdate;
        private DateTime _nextPersistenceUpdate;
        private DateTime _lastAlphaCountSampleDateUtc;
        private DateTime _nextChartSampleAlgorithmTimeUtc;

        private bool _isNotFrameworkAlgorithm;
        private IMessagingHandler _messagingHandler;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly ConcurrentQueue<Packet> _messages = new ConcurrentQueue<Packet>();

        private readonly Chart _totalAlphaCountPerSymbolChart = new Chart("Alpha Assets");          // pie chart
        private readonly Chart _dailyAlphaCountPerSymbolChart = new Chart("Alpha Asset Breakdown"); // stacked area
        private readonly Series _totalAlphaCountSeries = new Series("Count", SeriesType.Bar, "#");

        private readonly Dictionary<AlphaScoreType, Series> _alphaScoreSeriesByScoreType = new Dictionary<AlphaScoreType, Series>();
        private readonly ConcurrentDictionary<Symbol, int> _dailyAlphaCountPerSymbol = new ConcurrentDictionary<Symbol, int>();
        private readonly ConcurrentDictionary<Symbol, int> _alphaCountPerSymbol = new ConcurrentDictionary<Symbol, int>();

        /// <inheritdoc />
        public bool IsActive => !_cancellationTokenSource?.IsCancellationRequested ?? false;

        /// <summary>
        /// Gets the current alpha runtime statistics
        /// </summary>
        public AlphaRuntimeStatistics RuntimeStatistics { get; } = new AlphaRuntimeStatistics();

        /// <summary>
        /// Gets or sets the runtime statistics updated. This is responsible for estimating alpha value as
        /// well as providing other KPIs found in <see cref="AlphaRuntimeStatistics"/>
        /// </summary>
        public IAlphaRuntimeStatisticsUpdater StatisticsUpdater { get; set; } = new AlphaRuntimeStatisticsUpdater();

        /// <summary>
        /// Gets the algorithm's unique identifier
        /// </summary>
        protected string AlgorithmId => Job.AlgorithmId;

        /// <summary>
        /// Gets whether or not the job is a live job
        /// </summary>
        protected bool LiveMode => Job is LiveNodePacket;

        /// <summary>
        /// Gets the algorithm job packet
        /// </summary>
        protected AlgorithmNodePacket Job { get; private set; }

        /// <summary>
        /// Gets the algorithm instance
        /// </summary>
        protected IAlgorithm Algorithm { get; private set; }

        /// <summary>
        /// Gets or sets the interval at which the alphas are persisted
        /// </summary>
        protected TimeSpan PersistenceUpdateInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets the interval at which alpha updates are sent to the messaging handler
        /// </summary>
        protected TimeSpan MessagingUpdateInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Gets or sets the interval at which alpha charts are updated. This is in realtion to algorithm time.
        /// </summary>
        protected TimeSpan ChartUpdateInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets the alpha manager instance used to manage the analysis of algorithm alphas
        /// </summary>
        protected AlphaManager AlphaManager { get; private set; }

        /// <inheritdoc />
        public virtual void Initialize(AlgorithmNodePacket job, IAlgorithm algorithm, IMessagingHandler messagingHandler, IApi api)
        {
            // initializing these properties just in case, doens't hurt to have them populated
            Job = job;
            Algorithm = algorithm;
            _messagingHandler = messagingHandler;
            _isNotFrameworkAlgorithm = !algorithm.IsFrameworkAlgorithm;
            if (_isNotFrameworkAlgorithm)
            {
                return;
            }

            AlphaManager = CreateAlphaManager();

            // wire events to update runtime statistics at key moments in alpha life cycle (new/period end/analysis end)
            AlphaManager.AlphaReceived += (sender, context) => StatisticsUpdater.OnAlphaReceived(RuntimeStatistics, context);
            AlphaManager.AlphaClosed += (sender, context) => StatisticsUpdater.OnAlphaClosed(RuntimeStatistics, context);
            AlphaManager.AlphaAnalysisCompleted += (sender, context) => StatisticsUpdater.OnAlphaAnalysisCompleted(RuntimeStatistics, context);

            algorithm.AlphasGenerated += (algo, collection) => OnAlphasGenerated(collection);

            // chart for average scores over sample period
            var scoreChart = new Chart("Alpha");
            foreach (var scoreType in ScoreTypes)
            {
                var series = new Series($"{scoreType} Score", SeriesType.Line, "%");
                scoreChart.AddSeries(series);
                _alphaScoreSeriesByScoreType[scoreType] = series;
            }

            // chart for prediction count over sample period
            var predictionCount = new Chart("Alpha Count");
            predictionCount.AddSeries(_totalAlphaCountSeries);

            Algorithm.AddChart(scoreChart);
            Algorithm.AddChart(predictionCount);
            Algorithm.AddChart(_totalAlphaCountPerSymbolChart);
            // removing this for now, not sure best way to display this data
            //Algorithm.AddChart(_dailyAlphaCountPerSymbolChart);
        }

        /// <inheritdoc />
        public void OnAfterAlgorithmInitialized(IAlgorithm algorithm)
        {
            if (_isNotFrameworkAlgorithm)
            {
                return;
            }

            _nextChartSampleAlgorithmTimeUtc = algorithm.UtcTime + ChartUpdateInterval;
            _lastAlphaCountSampleDateUtc = (algorithm.Time.RoundDown(Time.OneDay) + Time.OneDay).ConvertToUtc(algorithm.TimeZone);

            if (LiveMode)
            {
                // live mode we'll sample each minute
                ChartUpdateInterval = Time.OneMinute;
            }
            else
            {
                // space out backtesting samples evenly
                var backtestPeriod = algorithm.EndDate - algorithm.StartDate;
                ChartUpdateInterval = TimeSpan.FromTicks(backtestPeriod.Ticks / BacktestChartSamples);
            }
        }

        /// <inheritdoc />
        public virtual void ProcessSynchronousEvents()
        {
            if (_isNotFrameworkAlgorithm)
            {
                return;
            }

            if (Algorithm.UtcTime.Date > _lastAlphaCountSampleDateUtc)
            {
                _lastAlphaCountSampleDateUtc = Algorithm.UtcTime.Date;

                // populate charts with the daily alpha counts per symbol, resetting our storage
                var sumPredictions = PopulateChartWithSeriesPerSymbol(_dailyAlphaCountPerSymbol, _dailyAlphaCountPerSymbolChart, SeriesType.StackedArea);
                _dailyAlphaCountPerSymbol.Clear();

                // add sum of daily alpha counts to the total alpha count series
                _totalAlphaCountSeries.AddPoint(Algorithm.UtcTime.Date, sumPredictions, LiveMode);

                // populate charts with the total alpha counts per symbol, no need to reset
                PopulateChartWithSeriesPerSymbol(_alphaCountPerSymbol, _totalAlphaCountPerSymbolChart, SeriesType.Pie);
            }

            // before updating scores, emit chart points for the previous sample period
            if (Algorithm.UtcTime >= _nextChartSampleAlgorithmTimeUtc)
            {
                try
                {
                    // verify these scores have been computed before taking the first sample
                    if (RuntimeStatistics.RollingAveragedPopulationScore.UpdatedTimeUtc != default(DateTime))
                    {
                        // sample the rolling averaged population scores
                        foreach (var scoreType in ScoreTypes)
                        {
                            var score = 100 * RuntimeStatistics.RollingAveragedPopulationScore.GetScore(scoreType);
                            _alphaScoreSeriesByScoreType[scoreType].AddPoint(Algorithm.UtcTime, (decimal) score, LiveMode);
                        }
                        _nextChartSampleAlgorithmTimeUtc = Algorithm.UtcTime + ChartUpdateInterval;
                    }
                }
                catch (Exception err)
                {
                    Log.Error(err);
                }
            }

            try
            {
                // update scores in line with the algo thread to ensure a consistent read of security data
                // this will manage marking alphas as closed as well as performing score updates
                AlphaManager.UpdateScores();
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }

        /// <inheritdoc />
        public virtual void Run()
        {
            if (_isNotFrameworkAlgorithm)
            {
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();

            // run until cancelled AND we're done processing messages
            while (!_cancellationTokenSource.IsCancellationRequested || !_messages.IsEmpty)
            {
                try
                {
                    ProcessAsynchronousEvents();
                }
                catch (Exception err)
                {
                    Log.Error(err);
                    throw;
                }

                Thread.Sleep(50);
            }

            // persist alphas at exit
            StoreAlphas();
        }

        /// <inheritdoc />
        public void Exit()
        {
            if (_isNotFrameworkAlgorithm)
            {
                return;
            }

            // send final alpha scoring updates before we exit
            _messages.Enqueue(new AlphaPacket
            {
                AlgorithmId = AlgorithmId,
                Alphas = AlphaManager.GetUpdatedContexts().Select(context => context.Alpha).ToList()
            });

            _cancellationTokenSource.Cancel(false);
        }

        /// <summary>
        /// Performs asynchronous processing, including broadcasting of alphas to messaging handler
        /// </summary>
        protected void ProcessAsynchronousEvents()
        {
            Packet packet;
            while (_messages.TryDequeue(out packet))
            {
                _messagingHandler.Send(packet);
            }

            // persist generated alphas to storage
            if (DateTime.UtcNow > _nextPersistenceUpdate)
            {
                StoreAlphas();
                _nextPersistenceUpdate = DateTime.UtcNow + PersistenceUpdateInterval;
            }

            // push updated alphas through messaging handler
            if (DateTime.UtcNow > _nextMessagingUpdate)
            {
                var alphas = AlphaManager.GetUpdatedContexts().Select(context => context.Alpha).ToList();
                if (alphas.Count > 0)
                {
                    _messages.Enqueue(new AlphaPacket
                    {
                        AlgorithmId = AlgorithmId,
                        Alphas = alphas
                    });
                }
                _nextMessagingUpdate = DateTime.UtcNow + MessagingUpdateInterval;
            }
        }

        /// <summary>
        /// Save alpha results to persistent storage
        /// </summary>
        protected virtual void StoreAlphas()
        {
            // default save all results to disk and don't remove any from memory
            // this will result in one file with all of the alphas/results in it
            var alphas = AlphaManager.AllAlphas.OrderBy(alpha => alpha.GeneratedTimeUtc).ToList();
            if (alphas.Count > 0)
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), AlgorithmId, "alpha-results.json");
                Directory.CreateDirectory(new FileInfo(path).DirectoryName);
                File.WriteAllText(path, JsonConvert.SerializeObject(alphas, Formatting.Indented));
            }
        }

        /// <summary>
        /// Handles the algorithm's <see cref="IAlgorithm.AlphasGenerated"/> event
        /// and broadcasts the new alpha using the messaging handler
        /// </summary>
        protected void OnAlphasGenerated(AlphaCollection collection)
        {
            // send message for newly created alphas
            Packet packet = new AlphaPacket(AlgorithmId, collection.Alphas);
            _messages.Enqueue(packet);

            AlphaManager.AddAlphas(collection);

            // aggregate alpha counts per symbol
            foreach (var grouping in collection.Alphas.GroupBy(alpha => alpha.Symbol))
            {
                // predictions for this time step
                var count = grouping.Count();

                // track daily assets
                _dailyAlphaCountPerSymbol.AddOrUpdate(grouping.Key, sym => count, (sym, cnt) => cnt + count);

                // track total assets for life of backtest
                _alphaCountPerSymbol.AddOrUpdate(grouping.Key, sym => count, (sym, cnt) => cnt + count);
            }
        }

        /// <summary>
        /// Creates the <see cref="AlphaManager"/> to manage the analysis of generated alphas
        /// </summary>
        /// <returns>A new alpha manager instance</returns>
        protected virtual AlphaManager CreateAlphaManager()
        {
            var scoreFunctionProvider = new DefaultAlphaScoreFunctionProvider();
            return new AlphaManager(new AlgorithmSecurityValuesProvider(Algorithm), scoreFunctionProvider, 0);
        }

        /// <summary>
        /// Creates series for each symbol and adds a value corresponding to the specified data
        /// </summary>
        private int PopulateChartWithSeriesPerSymbol(ConcurrentDictionary<Symbol, int> data, Chart chart, SeriesType seriesType)
        {
            var sum = 0;
            foreach (var kvp in data)
            {
                var symbol = kvp.Key;
                var count = kvp.Value;

                Series series;
                if (!chart.Series.TryGetValue(symbol.Value, out series))
                {
                    series = new Series(symbol.Value, seriesType, "#");
                    chart.Series.Add(series.Name, series);
                }

                sum += count;
                series.AddPoint(Algorithm.UtcTime, count, LiveMode);
            }
            return sum;
        }
    }
}