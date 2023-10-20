using Newtonsoft.Json;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Logging;
using BingX.API;
using BingX.API.Constants;
using BingX.API.Entity.Spot.MarketInterface;
using BingX.API.Entity.Spot.SocketAPI.WebSocketMarketData;
using BingX.API.Entity.Spot.SpotAccount;
using BingX.API.Helpers;
//using BingX.Spot.Entity;
using BingX.Exceptions;
using BingX.Interfaces;
using OsEngine.Market.Servers.Entity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WebSocket4Net;
using BingX.API.Url;
using OsEngine.Market.Servers.BingX.BingXSpot.Entity;

namespace OsEngine.Market.Servers.BingX.BingXSpot
{
    public class BingXServerSpot : AServer
    {
        public BingXServerSpot()
        {

            BingXServerSpotRealization realization = new BingXServerSpotRealization();
            ServerRealization = realization;

            CreateParameterString(OsLocalization.Market.ServerParamPublicKey, "");
            CreateParameterPassword(OsLocalization.Market.ServerParamSecretKey, "");
           // CreateParameterPassword(OsLocalization.Market.ServerParamPassphrase, "");
        }

        public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf)
        {
            return ((BingXServerSpotRealization)ServerRealization).GetCandleHistory(nameSec, tf, false, 0, DateTime.Now);
        }
    }

    public class BingXServerSpotRealization : IServerRealization
    {
        public event Action<string, LogMessageType> LogMessageEvent;
        private Request Request { get; set; } = null;
        private string LicenceKey { get; set; }
        private DateTime TimeLastLicenceKey { get; set; }
        private LoggerMsg Logger { get; set; }

        #region 1 Constructor, Status, Connection

        public BingXServerSpotRealization()
        {
            ServerStatus = ServerConnectStatus.Disconnect;
        }

        public DateTime ServerTime { get; set; }


        public void Connect()
        {
            Logger = new LoggerMsg(LogMessageEvent);
            ConnectAsync().GetAwaiter().GetResult();
        }

        public async Task ConnectAsync()
        {
            //string tB = "{\"code\":0,\"timestamp\":1697479694862,\"data\":[[1697479620000,0.001775,0.001776,0.001774,0.001774,20571.00,1697479679999,36.51]]}";
            //string tB = "{\"code\":0,\"data\":{\"asks\":[[\"2.102\",\"0.091\"],[\"1.971\",\"0.665\"]]},\"dataType\":\"TONCOIN-USDT@depth\",\"success\":true}";
            //var res = JsonConvert.DeserializeObject<MarketDepthDataResponse>(tB);
            //if (res.Data.Asks != null)
            //{
            //   return;
            //}

            IsDispose = false;
            PublicKey = ((ServerParameterString)ServerParameters[0]).Value;
            SeckretKey = ((ServerParameterPassword)ServerParameters[1]).Value;
            Request = new Request(PublicKey, SeckretKey, Logger);

            HttpClient httpPublicClient = new HttpClient();
            try
            {
                var serverResponse = await Request.DoRequestAsync(UrlRequest.GetServerTime);

                if (serverResponse != null && serverResponse.Code == 0) {
                    var t =  serverResponse.Data.ServerDateTime;

                    var LicenceKeyResponse = await Request.DoRequestAsync(UrlRequest.LicenceKey.ListenKey);
                    if (LicenceKeyResponse != null && !string.IsNullOrEmpty(LicenceKeyResponse.ListenKey))
                    {
                        LicenceKey = LicenceKeyResponse.ListenKey;
                        TimeLastLicenceKey = DateTime.Now;
                        TimeLastSendPong = DateTime.Now;
                        TimeToUprdatePortfolio = DateTime.Now;
                        FIFOListWebSocketMessage = new ConcurrentQueue<string>();
                        StartCheckAliveWebSocket();
                        StartMessageReader();
                        CreateWebSocketConnection();
                        StartUpdatePortfolio();
                        return;
                    }
                                        
                    Logger.SendLogMsg("Connection is closed. Licence Key is not find.", LogMessageType.Error);
                    
                }
                else
                {
                    Logger.SendLogMsg("Connection can not be open.", LogMessageType.Error);
                }

            }
            catch {
                Logger.SendLogMsg("Network error. Connection can not be open.", LogMessageType.Error);
            }
            IsDispose = true;
            ServerStatus = ServerConnectStatus.Disconnect;
            DisconnectEvent();
        }

        public void Dispose()
        {
            try
            {
                _subscribledSecutiries.Clear();
                DeleteWebsocketConnection();
            }
            catch (RequestException) { }
            catch (Exception e)
            {
                Logger.HandlerExeption(e);
            }

            IsDispose = true;
            FIFOListWebSocketMessage = new ConcurrentQueue<string>();

            if (ServerStatus != ServerConnectStatus.Disconnect)
            {
                ServerStatus = ServerConnectStatus.Disconnect;
                DisconnectEvent();
            }
        }

        private bool IsDispose;

        public ServerType ServerType
        {
            get { return ServerType.BingXSpot; }
        }

        public ServerConnectStatus ServerStatus { get; set; }

        public event Action ConnectEvent;

        public event Action DisconnectEvent;


        #endregion

        #region 2 Properties

        public List<IServerParameter> ServerParameters { get; set; }

        private string PublicKey;

        private string SeckretKey;


        #endregion

        #region 3 Securities

        public void GetSecurities()
        {
            try
            {
                var products = Request.DoRequest(UrlRequest.Spot.MarketInterface.QuerySymbols);

                if (products?.Code == 0 &&  products.Data?.Symbols?.Count > 0)
                {
                    Console.WriteLine(products.Data.Symbols.FirstOrDefault()?.Symbol);

                    List<Security> securities = new List<Security>();
                    foreach(var item in products.Data?.Symbols)
                    {
                        if (item.Status == 1) // is online
                        {
                            Security newSecurity = new Security
                            {
                                Exchange = ServerType.BingXSpot.ToString(),
                                DecimalsVolume = DecimalHelper.CountDecimalDigits(item.StepSize),
                                Lot = item.StepSize,
                                Name = item.Symbol,
                                NameFull = item.Symbol,
                                NameClass = item.Symbol,
                                NameId = item.Symbol,
                                SecurityType = SecurityType.CurrencyPair,
                                Decimals = DecimalHelper.CountDecimalDigits(item.TickSize), 
                                PriceStep = item.TickSize,
                                PriceStepCost = item.TickSize,
                                State = SecurityStateType.Activ                                
                            };
                            securities.Add(newSecurity);
                        }
                    }
                   
                    SecurityEvent(securities);
                }
            }
            catch (RequestException) { }
            catch (Exception e)
            {
                Logger.HandlerExeption(e);
            }
        }


        public event Action<List<Security>> SecurityEvent;

        #endregion

        #region 4 Portfolios

        private DateTime TimeToUprdatePortfolio = DateTime.Now;

        public void GetPortfolios()
        {
            CreateQueryPortfolio(true);
        }

        private void StartUpdatePortfolio()
        {
            Thread thread = new Thread(UpdatingPortfolio);
            thread.IsBackground = true;
            thread.Name = "UpdatingPortfolio";
            thread.Start();
        }

        private void UpdatingPortfolio()
        {
            while (IsDispose == false)
            {
                Thread.Sleep(200);

                if (TimeToUprdatePortfolio.AddSeconds(30) < DateTime.Now)
                {
                    CreateQueryPortfolio(false);
                    TimeToUprdatePortfolio = DateTime.Now;
                }
            }
        }

        public event Action<List<Portfolio>> PortfolioEvent;

        #endregion

        #region 5 Data

        public List<Trade> GetTickDataToSecurity(Security security, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            return null;
        }

        public List<Candle> GetCandleDataToSecurity(Security security, TimeFrameBuilder timeFrameBuilder, DateTime startTime, DateTime endTime, DateTime actualTime)
        {
            int countNeedToLoad = GetCountCandlesFromSliceTime(startTime, endTime, timeFrameBuilder.TimeFrameTimeSpan);
            return GetCandleHistory(security.NameFull, timeFrameBuilder.TimeFrameTimeSpan, true, countNeedToLoad, endTime);
        }

        public List<Candle> GetCandleHistory(string nameSec, TimeSpan tf, bool IsOsData, int CountToLoad, DateTime timeEnd)
        {
            string stringInterval = GetStringInterval(tf);
            int CountToLoadCandle = GetCountCandlesToLoad();


            List<Candle> candles = new List<Candle>();
            DateTime TimeToRequest = DateTime.UtcNow;

            if (IsOsData == true)
            {
                CountToLoadCandle = CountToLoad;
                TimeToRequest = timeEnd;
            }

            do
            {
                int limit = CountToLoadCandle;
                if (CountToLoadCandle > 1440)
                {
                    limit = 1440;
                }

                List<Candle> rangeCandles = new List<Candle>();

                rangeCandles = CreateQueryCandles(nameSec, stringInterval, limit, TimeToRequest.AddSeconds(10));

                rangeCandles.Reverse();

                candles.AddRange(rangeCandles);

                if (candles.Count != 0)
                {
                    TimeToRequest = candles[candles.Count - 1].TimeStart;
                }

                CountToLoadCandle -= limit;

            } while (CountToLoadCandle > 0);

            candles.Reverse();
            return candles;
        }

        private int GetCountCandlesFromSliceTime(DateTime startTime, DateTime endTime, TimeSpan tf)
        {
            if (tf.Hours != 0)
            {
                var totalHour = tf.TotalHours;
                TimeSpan TimeSlice = endTime - startTime;

                return Convert.ToInt32(TimeSlice.TotalHours / totalHour);
            }
            else
            {
                var totalMinutes = tf.Minutes;
                TimeSpan TimeSlice = endTime - startTime;
                return Convert.ToInt32(TimeSlice.TotalMinutes / totalMinutes);
            }
        }

        private int GetCountCandlesToLoad()
        {
            var server = (AServer)ServerMaster.GetServers().Find(server => server.ServerType == ServerType.BingXSpot);

            for (int i = 0; i < server.ServerParameters.Count; i++)
            {
                if (server.ServerParameters[i].Name.Equals("Candles to load"))
                {
                    var Param = (ServerParameterInt)server.ServerParameters[i];
                    return Param.Value;
                }
            }

            return 300;
        }

        private string GetStringInterval(TimeSpan tf)
        {
            if (tf.Minutes != 0)
            {
                return $"{tf.Minutes}m";
            }
            else
            {
                return $"{tf.Hours}h";
            }
        }

        #endregion

        #region 6 WebSocket creation

        private WebSocket webSocket;

        private string WebSocketUrl = "wss://open-api-ws.bingx.com/market";

        private string _socketLocker = "webSocketLockerBingX";

        private void CreateWebSocketConnection()
        {
           // System.Net.ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            webSocket = new WebSocket(WebSocketUrl, "", null,null,"","",WebSocketVersion.None,null, System.Security.Authentication.SslProtocols.Tls12);
            //webSocket.EnableAutoSendPing = true;
            //webSocket.AutoSendPingInterval = 5;
            webSocket.Opened += WebSocket_Opened;
            webSocket.Closed += WebSocket_Closed;
            // webSocket.MessageReceived += WebSocket_MessageReceived;
            webSocket.DataReceived += WebSocket_DataReceived;
            webSocket.Error += WebSocket_Error;

            webSocket.Open();

        }

        private void DeleteWebsocketConnection()
        {
            if (webSocket != null)
                lock (_socketLocker)
                {
                    {
                        try
                        {
                            webSocket.Close();
                        }
                        catch
                        {
                            // ignore
                        }

                        webSocket.Opened -= WebSocket_Opened;
                        webSocket.Closed -= WebSocket_Closed;
                        webSocket.Error -= WebSocket_Error;
                        webSocket = null;
                    }
            }
        }

        #endregion

        #region 7 WebSocket events

        private void WebSocket_Error(object sender, SuperSocket.ClientEngine.ErrorEventArgs e)
        {
            var error = (SuperSocket.ClientEngine.ErrorEventArgs)e;
            if (error.Exception != null)
            {
                Logger.HandlerExeption(error.Exception);
            }
        }

        private void WebSocket_DataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e == null || e.Data == null || e.Data.LongLength == 0)
            {
                Logger.SendLogMsg("WebSocket_DataReceived is empty", LogMessageType.Error);
            }
            try
            {                    
                if (FIFOListWebSocketMessage.Count > GenericConst.MaxMessagesInQueryList)
                {
                    FIFOListWebSocketMessage.TryDequeue(out var message);
                    //Logger.SendLogMsg($"A large server query. Remove {message}", LogMessageType.Error);
                }
                var response = GZipHelper.DeCompress(e.Data);
              
                Console.WriteLine("Server said: " + response);
                if (response.Contains("\"ping\":"))
                {

                    lock (_socketLocker)
                    {
                        LastPingMessage = response;
                    }

                    //    var webSocket = sender as WebSocket;
                    //    var pongMessage = response.Replace("ping", "pong");
                    //    webSocket.Send(pongMessage);
                    //    Console.WriteLine("Clien said: " + pongMessage);
                }
                else
                {
                    FIFOListWebSocketMessage.Enqueue(response);
                }
            }
            catch(Exception ex)
            {
                Logger.SendLogMsg($"WebSocket_DataReceived can no GZip DeCompress. {ex.Message}", LogMessageType.Error);
            }         
        }

        private void WebSocket_Closed(object sender, EventArgs e)
        {
            if (IsDispose == false)
            {
                Logger.SendLogMsg("Connection Closed by BingX. WebSocket Closed Event", LogMessageType.Error);
                ServerStatus = ServerConnectStatus.Disconnect;
                DisconnectEvent();
            }
        }
        
        private void WebSocket_Opened(object sender, EventArgs e)
        {
            var webSocket = sender as WebSocket;
            CreateAuthMessageWebSocket(webSocket);
            Logger.SendLogMsg("Connection Open", LogMessageType.System);
            ServerStatus = ServerConnectStatus.Connect;
            ConnectEvent();
        }

        #endregion

        #region 8 WebSocket check alive

        private DateTime TimeLastSendPong = DateTime.Now;

        private string LastPingMessage { get; set; }

        private void StartCheckAliveWebSocket()
        {
            Thread thread = new Thread(CheckAliveWebSocket);
            thread.IsBackground = true;
            thread.Name = "CheckAliveWebSocket";
            thread.Start();
        }

        private void CheckAliveWebSocket()
        {
            int NO_PING_IN_SEC = 50;
            while (IsDispose == false)
            {
                Thread.Sleep(3000);

                if (webSocket != null &&
                    (webSocket.State == WebSocketState.Open ||
                    webSocket.State == WebSocketState.Connecting)
                    )
                {
                    if (LastPingMessage != null && TimeLastSendPong.AddSeconds(4) <= DateTime.Now)
                    {
                        lock (_socketLocker)
                        {
                            var pongMessage = LastPingMessage.Replace("ping", "pong");
                            webSocket.Send(pongMessage);
                            Console.WriteLine("Client said: " + pongMessage);
                            LastPingMessage = null;
                            TimeLastSendPong = DateTime.Now;
                        }
                    }
                    else if(TimeLastSendPong.AddSeconds(NO_PING_IN_SEC) <= DateTime.Now)
                    {
                        Logger.SendLogMsg($"No ping more than {NO_PING_IN_SEC} sec. WebSocket Closed Event", LogMessageType.Error);
                        Dispose();
                    }
                }
                else
                {
                    Dispose();
                }
            }
        }

        #endregion

        #region 9 WebSocket security subscrible

        private RateGate rateGateSubscrible = new RateGate(1, TimeSpan.FromMilliseconds(300));

        public void Subscrible(Security security)
        {
            try
            {
                rateGateSubscrible.WaitToProceed();
                CreateSubscribleSecurityMessageWebSocket(security);
            }
            catch (RequestException) { }
            catch (Exception e)
            {
                Logger.HandlerExeption(e);
            }
        }

        #endregion

        #region 10 WebSocket parsing the messages

        private void StartMessageReader()
        {
            Thread thread = new Thread(MessageReader);
            thread.IsBackground = true;
            thread.Name = "MessageReaderBingX";
            thread.Start();
        }

        private ConcurrentQueue<string> FIFOListWebSocketMessage = new ConcurrentQueue<string>();

        private void MessageReader()
        {
            Thread.Sleep(1000);

            while (IsDispose == false)
            {
                try
                {
                    if (FIFOListWebSocketMessage.IsEmpty)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    string message;

                    FIFOListWebSocketMessage.TryDequeue(out message);

                    if (message == null)
                    {
                        continue;
                    }

                    if (message.Equals("pong"))
                    {
                        continue;
                    }

                    MarketDepthDataResponse SubscribleState = null;

                    try
                    {
                        SubscribleState = JsonConvert.DeserializeObject<MarketDepthDataResponse>(message);
                        //SubscribleState = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageSubscrible());
                    }
                    catch (Exception error)
                    {
                        Logger.SendLogMsg("Error in message reader: " + error.ToString(), LogMessageType.Error);
                        Logger.SendLogMsg("message str: \n" + message, LogMessageType.Error);
                        continue;
                    }

                    if (SubscribleState.Code > 0)
                    {
                        Logger.SendLogMsg(SubscribleState.Code + "\n" +
                                SubscribleState.DebugMsg, LogMessageType.Error);
                        Dispose();
                        continue;
                    }
                    else
                    {
                        //ResponseWebSocketMessageAction<object> action = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageAction<object>());

                        //if (action.arg != null)
                        //{
                        //    if (action.arg.channel.Equals("books15"))
                        //    {
                        //        UpdateDepth(message);
                        //        continue;
                        //    }
                        //    if (action.arg.channel.Equals("trade"))
                        //    {
                        //        UpdateTrade(message);
                        //        continue;
                        //    }
                        //    if (action.arg.channel.Equals("orders"))
                        //    {
                        //        UpdateOrder(message);
                        //        continue;
                        //    }
                        //}
                    }
                }
                catch (RequestException) { }
                catch (Exception e)
                {
                    Logger.HandlerExeption(e);
                }
            }
        }

        private void UpdateTrade(string message)
        {
            ResponseWebSocketMessageAction<List<List<string>>> responseTrade = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageAction<List<List<string>>>());

            if (responseTrade == null)
            {
                return;
            }

            if (responseTrade.data == null)
            {
                return;
            }

            if (responseTrade.data.Count == 0)
            {
                return;
            }

            if (responseTrade.data[0] == null)
            {
                return;
            }

            if (responseTrade.data[0].Count < 2)
            {
                return;
            }

            Trade trade = new Trade();
            trade.SecurityNameCode = responseTrade.arg.instId;
            trade.Price = Convert.ToDecimal(responseTrade.data[0][1].Replace(".", ","));
            trade.Id = responseTrade.data[0][0];
            trade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(responseTrade.data[0][0]));
            trade.Volume = Convert.ToDecimal(responseTrade.data[0][2].Replace('.', ',').Replace(".", ","));
            trade.Side = responseTrade.data[0][3].Equals("buy") ? Side.Buy : Side.Sell;

            NewTradesEvent(trade);
        }

        private void UpdateDepth(string message)
        {
            ResponseWebSocketMessageAction<List<ResponseWebSocketDepthItem>> responseDepth = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageAction<List<ResponseWebSocketDepthItem>>());

            if (responseDepth.data == null)
            {
                return;
            }

            MarketDepth marketDepth = new MarketDepth();

            List<MarketDepthLevel> ascs = new List<MarketDepthLevel>();
            List<MarketDepthLevel> bids = new List<MarketDepthLevel>();

            marketDepth.SecurityNameCode = responseDepth.arg.instId;

            for (int i = 0; i < responseDepth.data[0].asks.Count; i++)
            {
                ascs.Add(new MarketDepthLevel()
                {
                    Ask = responseDepth.data[0].asks[i][1].ToString().ToDecimal(),
                    Price = responseDepth.data[0].asks[i][0].ToString().ToDecimal()
                });
            }

            for (int i = 0; i < responseDepth.data[0].bids.Count; i++)
            {
                bids.Add(new MarketDepthLevel()
                {
                    Bid = responseDepth.data[0].bids[i][1].ToString().ToDecimal(),
                    Price = responseDepth.data[0].bids[i][0].ToString().ToDecimal()
                });
            }

            marketDepth.Asks = ascs;
            marketDepth.Bids = bids;

            marketDepth.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(responseDepth.data[0].ts));


            MarketDepthEvent(marketDepth);
        }

        private void UpdateMytrade(string json)
        {
            ResponseMessageRest<List<ResponseMyTrade>> responseMyTrades = JsonConvert.DeserializeAnonymousType(json, new ResponseMessageRest<List<ResponseMyTrade>>());


            for (int i = 0; i < responseMyTrades.data.Count; i++)
            {
                ResponseMyTrade responseT = responseMyTrades.data[i];

                MyTrade myTrade = new MyTrade();

                myTrade.Time = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(responseT.cTime));
                myTrade.NumberOrderParent = responseT.orderId;
                myTrade.NumberTrade = responseT.fillId.ToString();
                myTrade.Price = responseT.fillPrice.ToDecimal();
                myTrade.SecurityNameCode = responseT.symbol.ToUpper().Replace("_SPBL", "");
                myTrade.Side = responseT.side.Equals("buy") ? Side.Buy : Side.Sell;


                if (string.IsNullOrEmpty(responseT.feeCcy) == false
                    && string.IsNullOrEmpty(responseT.fees) == false
                    && responseT.fees.ToDecimal() != 0)
                {// комиссия берёться в какой-то монете
                    string comissionSecName = responseT.feeCcy;

                    if (myTrade.SecurityNameCode.StartsWith("BGB")
                        || myTrade.SecurityNameCode.StartsWith(comissionSecName))
                    {
                        myTrade.Volume = responseT.fillQuantity.ToDecimal() + responseT.fees.ToDecimal();
                    }
                    else
                    {
                        myTrade.Volume = responseT.fillQuantity.ToDecimal();
                    }
                }
                else
                {// не известная монета комиссии. Берём весь объём
                    myTrade.Volume = responseT.fillQuantity.ToDecimal();
                }

                MyTradeEvent(myTrade);
            }

        }

        private void UpdateOrder(string message)
        {
            ResponseWebSocketMessageAction<List<ResponseWebSocketOrder>> Order = JsonConvert.DeserializeAnonymousType(message, new ResponseWebSocketMessageAction<List<ResponseWebSocketOrder>>());

            if (Order.data == null ||
                Order.data.Count == 0)
            {
                return;
            }

            for (int i = 0; i < Order.data.Count; i++)
            {
                var item = Order.data[i];

                OrderStateType stateType = GetOrderState(item.status);

                if (item.ordType.Equals("market") && stateType == OrderStateType.Activ)
                {
                    continue;
                }

                Order newOrder = new Order();
                newOrder.SecurityNameCode = item.instId.Replace("_SPBL", "");
                newOrder.TimeCallBack = TimeManager.GetDateTimeFromTimeStamp(Convert.ToInt64(item.cTime));

                if (!item.clOrdId.Equals(String.Empty) == true)
                {
                    try
                    {
                        newOrder.NumberUser = Convert.ToInt32(item.clOrdId);
                    }
                    catch
                    {
                        Logger.SendLogMsg("strage order num: " + item.clOrdId, LogMessageType.Error);
                        return;
                    }
                }

                newOrder.NumberMarket = item.ordId.ToString();
                newOrder.Side = item.side.Equals("buy") ? Side.Buy : Side.Sell;
                newOrder.State = stateType;
                newOrder.Volume = item.sz.Replace('.', ',').ToDecimal();
                newOrder.Price = item.avgPx.Replace('.', ',').ToDecimal();
                if (item.px != null)
                {
                    newOrder.Price = item.px.Replace('.', ',').ToDecimal();
                }
                newOrder.ServerType = ServerType.BingXSpot;
                newOrder.PortfolioNumber = "BingXSpot";

                if (stateType == OrderStateType.Done ||
                    stateType == OrderStateType.Patrial)
                {
                    // как только приходит ордер исполненный или частично исполненный триггер на запрос моего трейда по имени бумаги
                    CreateQueryMyTrade(newOrder.SecurityNameCode + "_SPBL", newOrder.NumberMarket);
                }
                MyOrderEvent(newOrder);
            }
        }

        private OrderStateType GetOrderState(string orderStateResponse)
        {
            OrderStateType stateType;

            switch (orderStateResponse)
            {
                case ("init"):
                case ("new"):
                    stateType = OrderStateType.Activ;
                    break;
                case ("partial-fill"):
                    stateType = OrderStateType.Patrial;
                    break;
                case ("full-fill"):
                    stateType = OrderStateType.Done;
                    break;
                case ("cancelled"):
                    stateType = OrderStateType.Cancel;
                    break;
                default:
                    stateType = OrderStateType.None;
                    break;
            }

            return stateType;
        }

        public event Action<Order> MyOrderEvent;

        public event Action<MyTrade> MyTradeEvent;

        public event Action<MarketDepth> MarketDepthEvent;

        public event Action<Trade> NewTradesEvent;

        #endregion

        #region 11 Trade

        private RateGate rateGateSendOrder = new RateGate(1, TimeSpan.FromMilliseconds(200));

        private RateGate rateGateCancelOrder = new RateGate(1, TimeSpan.FromMilliseconds(200));

        public void SendOrder(Order order)
        {
            rateGateSendOrder.WaitToProceed();
            string jsonRequest = JsonConvert.SerializeObject(new
            {
                symbol = order.SecurityNameCode + "_SPBL",
                side = order.Side.ToString().ToLower(),
                orderType = OrderPriceType.Limit.ToString().ToLower(),
                force = "normal",
                price = order.Price.ToString().Replace(",", "."),
                quantity = order.Volume.ToString().Replace(",", "."),
                clientOrderId = order.NumberUser
            });

            HttpResponseMessage responseMessage = CreatePrivateQuery("/api/spot/v1/trade/orders", "POST", null, jsonRequest);
            string JsonResponse = responseMessage.Content.ReadAsStringAsync().Result;
            ResponseMessageRest<object> stateResponse = JsonConvert.DeserializeAnonymousType(JsonResponse, new ResponseMessageRest<object>());

            if (responseMessage.StatusCode == System.Net.HttpStatusCode.OK)
            {
                if (stateResponse.code.Equals("00000") == true)
                {
                    // ignore
                }
                else
                {
                    CreateOrderFail(order);
                    Logger.SendLogMsg($"Code: {stateResponse.code}\n"
                        + $"Message: {stateResponse.msg}", LogMessageType.Error);
                }
            }
            else
            {
                CreateOrderFail(order);
                Logger.SendLogMsg($"Http State Code: {responseMessage.StatusCode}", LogMessageType.Error);

                if (stateResponse != null && stateResponse.code != null)
                {
                    Logger.SendLogMsg($"Code: {stateResponse.code}\n"
                        + $"Message: {stateResponse.msg}", LogMessageType.Error);
                }
            }

        }

        public void GetOrdersState(List<Order> orders)
        {

        }

        public void CancelAllOrders()
        {

        }

        public void CancelAllOrdersToSecurity(Security security)
        {
            rateGateCancelOrder.WaitToProceed();

            string jsonRequest = JsonConvert.SerializeObject(new
            {
                symbol = security.Name + "_SPBL"
            });

            HttpResponseMessage responseMessage = CreatePrivateQuery("/api/spot/v1/trade/cancel-order-v2", "POST", null, jsonRequest);
            string JsonResponse = responseMessage.Content.ReadAsStringAsync().Result;
            ResponseMessageRest<object> stateResponse = JsonConvert.DeserializeAnonymousType(JsonResponse, new ResponseMessageRest<object>());

            if (responseMessage.StatusCode == System.Net.HttpStatusCode.OK)
            {
                if (stateResponse.code.Equals("00000") == true)
                {
                    // ignore
                }
                else
                {
                    Logger.SendLogMsg($"Code: {stateResponse.code}\n"
                        + $"Message: {stateResponse.msg}", LogMessageType.Error);
                }
            }
            else
            {
                Logger.SendLogMsg($"Http State Code: {responseMessage.StatusCode}", LogMessageType.Error);

                if (stateResponse != null && stateResponse.code != null)
                {
                    Logger.SendLogMsg($"Code: {stateResponse.code}\n"
                        + $"Message: {stateResponse.msg}", LogMessageType.Error);
                }
            }


        }

        public void CancelOrder(Order order)
        {
            rateGateCancelOrder.WaitToProceed();

            string jsonRequest = JsonConvert.SerializeObject(new
            {
                symbol = order.SecurityNameCode + "_SPBL",
                orderId = order.NumberMarket
            });

            HttpResponseMessage responseMessage = CreatePrivateQuery("/api/spot/v1/trade/cancel-order", "POST", null, jsonRequest);
            string JsonResponse = responseMessage.Content.ReadAsStringAsync().Result;
            ResponseMessageRest<object> stateResponse = JsonConvert.DeserializeAnonymousType(JsonResponse, new ResponseMessageRest<object>());

            if (responseMessage.StatusCode == System.Net.HttpStatusCode.OK)
            {
                if (stateResponse.code.Equals("00000") == true)
                {
                    // ignore
                }
                else
                {
                    CreateOrderFail(order);
                    Logger.SendLogMsg($"Code: {stateResponse.code}\n"
                        + $"Message: {stateResponse.msg}", LogMessageType.Error);
                }
            }
            else
            {
                CreateOrderFail(order);
                Logger.SendLogMsg($"Http State Code: {responseMessage.StatusCode}", LogMessageType.Error);

                if (stateResponse != null && stateResponse.code != null)
                {
                    Logger.SendLogMsg($"Code: {stateResponse.code}\n"
                        + $"Message: {stateResponse.msg}", LogMessageType.Error);
                }
            }
        }

        public void ResearchTradesToOrders(List<Order> orders)
        {

        }

        private void CreateOrderFail(Order order)
        {
            order.State = OrderStateType.Fail;

            if (MyOrderEvent != null)
            {
                MyOrderEvent(order);
            }
        }

        #endregion

        #region 12 Queries

        private string BaseUrl = "https://open-api.bingx.com";

        private RateGate rateGateGetMyTradeState = new RateGate(1, TimeSpan.FromMilliseconds(200));

        HttpClient _httpPublicClient = new HttpClient();

        private List<string> _subscribledSecutiries = new List<string>();

        private void CreateSubscribleSecurityMessageWebSocket(Security security)
        {
            if (IsDispose)
            {
                return;
            }

            for (int i = 0; i < _subscribledSecutiries.Count; i++)
            {
                if (_subscribledSecutiries[i].Equals(security.Name))
                {
                    return;
                }
            }

            _subscribledSecutiries.Add(security.Name);

            lock (_socketLocker)
            {
                webSocket.Send(MarketDepthDataCommand.GetCommand(security.Name));
                //webSocket.Send($"{{\"op\": \"subscribe\",\"args\": [{{\"instType\": \"sp\",\"channel\": \"trade\",\"instId\": \"{security.Name}\"}}]}}");
                //webSocket.Send($"{{\"op\": \"subscribe\",\"args\": [{{ \"instType\": \"sp\",\"channel\": \"books15\",\"instId\": \"{security.Name}\"}}]}}");
               //webSocket.Send($"{{\"op\": \"subscribe\",\"args\": [{{\"channel\": \"orders\",\"instType\": \"spbl\",\"instId\": \"{security.Name}_SPBL\"}}]}}");
            }
        }

        private void CreateAuthMessageWebSocket(WebSocket webSocket)
        {
            if (webSocket != null)
            {
               // var CHANNEL = "{ \"id\":\"809be26b-11aa-46b2-ba25-43eab2586f1c\",\"reqType\": \"sub\",\"dataType\":\"BTC-USDT@trade\"}";
                webSocket.Send("pong");
            }

            //string TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            //string Sign = GenerateSignature(TimeStamp, "GET", "/user/verify", null, null, SeckretKey);

            //RequestWebsocketAuth requestWebsocketAuth = new RequestWebsocketAuth()
            //{
            //    op = "login",
            //    args = new List<AuthItem>()
            //     {
            //          new AuthItem()
            //          {
            //               apiKey = PublicKey,
            //               timestamp = TimeStamp,
            //                sign = Sign
            //          }
            //     }
            //};

            //string AuthJson = JsonConvert.SerializeObject(requestWebsocketAuth);
            //webSocket.Send(AuthJson);
        }

        private void CreateQueryPortfolio(bool IsUpdateValueBegin)
        {
            try
            {
                var queryAssets = UrlRequest.Spot.SpotAccount.QueryAssets;
                queryAssets.Request = new QueryAssetsRequest()
                {
                    //
                };

                var queryAssetsResponse = Request.DoRequest(queryAssets);

                Portfolio portfolio = new Portfolio();
                portfolio.Number = "BingXSpot";
                portfolio.ValueBegin = 1;
                portfolio.ValueCurrent = 1;

                foreach (var item in queryAssetsResponse.Data.Balances)
                {
                    var pos = new PositionOnBoard()
                    {
                        PortfolioName = "BingXSpot",
                        SecurityNameCode = item.Asset,
                        ValueBlocked = item.Locked.ToDecimal(),
                        ValueCurrent = item.Free.ToDecimal()
                    };

                    if (IsUpdateValueBegin)
                    {
                        pos.ValueBegin = item.Free.ToDecimal();
                    }

                    portfolio.SetNewPosition(pos);
                }

                PortfolioEvent(new List<Portfolio> { portfolio });
            }
            catch (RequestException) { }
            catch (Exception e) 
            {
                Logger.HandlerExeption(e);
            }
        }

        private void UpdatePorfolio(string json, bool IsUpdateValueBegin)
        {
            ResponseMessageRest<List<ResponseAsset>> assets = JsonConvert.DeserializeAnonymousType(json, new ResponseMessageRest<List<ResponseAsset>>());

            Portfolio portfolio = new Portfolio();
            portfolio.Number = "BingXSpot";
            portfolio.ValueBegin = 1;
            portfolio.ValueCurrent = 1;

            foreach (var item in assets.data)
            {
                var pos = new PositionOnBoard()
                {
                    PortfolioName = "BingXSpot",
                    SecurityNameCode = item.coinName,
                    ValueBlocked = item.frozen.ToDecimal(),
                    ValueCurrent = item.available.ToDecimal()
                };

                if (IsUpdateValueBegin)
                {
                    pos.ValueBegin = item.available.ToDecimal();
                }

                portfolio.SetNewPosition(pos);
            }

            PortfolioEvent(new List<Portfolio> { portfolio });
        }

        private void CreateQueryMyTrade(string nameSec, string OrdId)
        {
            rateGateGetMyTradeState.WaitToProceed();

            string json = JsonConvert.SerializeObject(new
            {
                symbol = nameSec,
                orderId = OrdId
            });

            HttpResponseMessage responseMessage = CreatePrivateQuery("/api/spot/v1/trade/fills", "POST", null, json);
            string JsonResponse = responseMessage.Content.ReadAsStringAsync().Result;

            ResponseMessageRest<object> stateResponse = JsonConvert.DeserializeAnonymousType(JsonResponse, new ResponseMessageRest<object>());

            if (responseMessage.StatusCode == System.Net.HttpStatusCode.OK)
            {
                if (stateResponse.code.Equals("00000") == true)
                {
                    UpdateMytrade(JsonResponse);
                }
                else
                {
                    Logger.SendLogMsg($"Code: {stateResponse.code}\n"
                        + $"Message: {stateResponse.msg}", LogMessageType.Error);
                }
            }
            else
            {
                Logger.SendLogMsg($"Http State Code: {responseMessage.StatusCode}", LogMessageType.Error);

                if (stateResponse != null && stateResponse.code != null)
                {
                    Logger.SendLogMsg($"Code: {stateResponse.code}\n"
                        + $"Message: {stateResponse.msg}", LogMessageType.Error);
                }
            }

        }

        private List<Candle> CreateQueryCandles(string nameSec, string stringInterval, int limit, DateTime timeEndToLoad)
        {
            var chartData = UrlRequest.Spot.MarketInterface.CandlestickChartData;
            chartData.Request = new CandlestickChartDataRequest()
            {
                Symbol = nameSec,
                Interval = stringInterval,
                Limit = limit,
                //StartTime = ((DateTimeOffset)timeEndToLoad).ToUnixTimeMilliseconds(),
                EndTime = TimeManager.GetTimeStampMilliSecondsToDateTime(timeEndToLoad),
            };

            var CandlestickResponse = Request.DoRequest(chartData);

            List<Candle> candles = new List<Candle>();

            foreach (var item in CandlestickResponse.Data)
            {
                candles.Add(new Candle()
                {
                    Close = CandlestickResponse.GetData(item, CandlestickChartData.ClosePrice),
                    High = CandlestickResponse.GetData(item, CandlestickChartData.MaxPrice),
                    Low = CandlestickResponse.GetData(item, CandlestickChartData.MinPrice),
                    Open = CandlestickResponse.GetData(item, CandlestickChartData.OpenPrice),
                    Volume = CandlestickResponse.GetData(item, CandlestickChartData.FilledPrice),
                    State = CandleState.Finished,
                    TimeStart = TimeManager.GetDateTimeFromTimeStamp((long)CandlestickResponse.GetData(item, CandlestickChartData.CandlestickChartOpenTime)),
                });            
            }
            return candles;
        }

        private HttpResponseMessage CreatePrivateQuery(string path, string method, string queryString, string body)
        {

            Logger.SendLogMsg($"PrivateQuery {path} {method} {queryString} {body}", LogMessageType.System);
            return new HttpResponseMessage(HttpStatusCode.OK);
            string requestPath = path;
            string url = $"{BaseUrl}{requestPath}";
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string signature = GenerateSignature(timestamp, method, requestPath, queryString, body, SeckretKey);

            HttpClient httpClient = new HttpClient();

            httpClient.DefaultRequestHeaders.Add("ACCESS-KEY", PublicKey);
            httpClient.DefaultRequestHeaders.Add("ACCESS-SIGN", signature);
            httpClient.DefaultRequestHeaders.Add("ACCESS-TIMESTAMP", timestamp);
            httpClient.DefaultRequestHeaders.Add("X-CHANNEL-API-CODE", "6yq7w");

            if (method.Equals("POST"))
            {
                return httpClient.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json")).Result;
            }
            else
            {
                return httpClient.GetAsync(url).Result;
            }
        }

        private string GenerateSignature(string timestamp, string method, string requestPath, string queryString, string body, string secretKey)
        {
            return "";
        }

        #endregion
    }
}
