﻿using System.Collections.Generic;


namespace OsEngine.Market.Servers.BingX.BingXFutures.Entity
{
    public class RequestWebsocketAuth
    {
        public string op;
        public List<AuthItem> args;
    }

    public class AuthItem
    {
        public string apiKey;
        public string passphrase;
        public string timestamp;
        public string sign;
    }
}
