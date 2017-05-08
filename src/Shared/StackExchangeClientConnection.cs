﻿//
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.
//

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Web.SessionState;
using StackExchange.Redis;

namespace Microsoft.Web.Redis
{
    internal class StackExchangeClientConnection : IRedisClientConnection
    {

        ConnectionMultiplexer _redisMultiplexer;
        IDatabase _connection;
        ProviderConfiguration _configuration;
        ConfigurationOptions _configOption;
        private ConfigurationOptions _sentinelConfiguration;
        private ConnectionMultiplexer _sentinelConnection;
        private RedisUtility redisUtility;

      public StackExchangeClientConnection(ProviderConfiguration configuration)
        {
            _configuration = configuration;
            _configOption = ParseConfiguration(_configuration);
            if (!string.IsNullOrEmpty(_configOption.ServiceName))
            {
              _sentinelConfiguration = CreateSentinellConfiguration(_configOption);
              ModifyEndpointsForSentinelConfiguration(_configOption);
            }
            ConnectToRedis(_configuration);
            this.redisUtility = new RedisUtility(configuration);
            this.configuration = configuration;
            ConfigurationOptions configOption;
        }

        private void ConnectToRedis(ProviderConfiguration configuration)
        {
          _redisMultiplexer = LogUtility.logger == null
            ? ConnectionMultiplexer.Connect(_configOption)
            : ConnectionMultiplexer.Connect(_configOption, LogUtility.logger);

          _connection = _redisMultiplexer.GetDatabase(configuration.DatabaseId);
        }

        private ConfigurationOptions ParseConfiguration(ProviderConfiguration configuration)
        {
          ConfigurationOptions configOption;
          // If connection string is given then use it otherwise use individual options
          if (!string.IsNullOrEmpty(configuration.ConnectionString))
          {
            configOption = ConfigurationOptions.Parse(configuration.ConnectionString);
          }
          else
          {
            configOption = new ConfigurationOptions();
            if (configuration.Port == 0)
            {
              configOption.EndPoints.Add(configuration.Host);
              //configOption = ConfigurationOptions.Parse(configuration.ConnectionString);
                // Setting explicitly 'abortconnect' to false. It will overwrite customer provided value for 'abortconnect'
                // As it doesn't make sense to allow to customer to set it to true as we don't give them access to ConnectionMultiplexer
                // in case of failure customer can not create ConnectionMultiplexer so right choice is to automatically create it by providing AbortOnConnectFail = false
                //configOption.AbortOnConnectFail = false;
            }
            else
            {
              configOption.EndPoints.Add(configuration.Host + ":" + configuration.Port);
            }
              configOption.Password = configuration.AccessKey;
              configOption.Ssl = configuration.UseSsl;
              configOption.AbortOnConnectFail = false;

            if (configuration.ConnectionTimeoutInMilliSec != 0)
            {
              configOption.ConnectTimeout = configuration.ConnectionTimeoutInMilliSec;
            }

            if (configuration.OperationTimeoutInMilliSec != 0)
            {
              configOption.SyncTimeout = configuration.OperationTimeoutInMilliSec;
            }
          }
          return configOption;
        }

          private void ModifyEndpointsForSentinelConfiguration(ConfigurationOptions configOption)
          {
              ConnectToSentinel();
              EndPoint masterEndPoint = _sentinelConnection.GetServer(_sentinelConnection.GetEndPoints().First()).SentinelGetMasterAddressByName(_sentinelConfiguration.ServiceName);
              configOption.EndPoints.Clear();
              configOption.EndPoints.Add(masterEndPoint);
          }

          private ConfigurationOptions CreateSentinellConfiguration(ConfigurationOptions configOption)
          {
            var sentinelConfiguration = new ConfigurationOptions
            {
              CommandMap = CommandMap.Sentinel,
              TieBreaker = "",
              ServiceName = configOption.ServiceName,
              SyncTimeout = configOption.SyncTimeout,
            };
            foreach (var endPoint in configOption.EndPoints)
            {
              sentinelConfiguration.EndPoints.Add(endPoint);
            }
            return sentinelConfiguration;
          }

          private void ConnectToSentinel()
          {
            _sentinelConnection = ConnectionMultiplexer.Connect(_sentinelConfiguration);
            var subscriber = _sentinelConnection.GetSubscriber();
            subscriber.Subscribe("+switch-master", MasterWasSwitched);
            _sentinelConnection.ConnectionFailed += SentinelConnectionFailed;
          }

          private void SentinelConnectionFailed(object sender, ConnectionFailedEventArgs e)
          {
            _sentinelConnection.ConnectionFailed -= SentinelConnectionFailed;
            _sentinelConnection.Close();
            ConnectToSentinel();
          }

        private void MasterWasSwitched(RedisChannel redisChannel, RedisValue redisValue)
        {
          if (redisValue.IsNullOrEmpty) return;
          var message = redisValue.ToString();
          var messageParts = message.Split(' ');
          var ip = IPAddress.Parse(messageParts[3]);
          var port = int.Parse(messageParts[4]);
          EndPoint newMasterEndpoint = new IPEndPoint(ip, port);
          if (_configOption.EndPoints.Any() && newMasterEndpoint == _configOption.EndPoints[0]) return;
          _configOption.EndPoints.Clear();
          _configOption.EndPoints.Add(newMasterEndpoint);

          _redisMultiplexer = LogUtility.logger == null
            ? ConnectionMultiplexer.Connect(_configOption)
            : ConnectionMultiplexer.Connect(_configOption, LogUtility.logger);

          _connection = _redisMultiplexer.GetDatabase(_configuration.DatabaseId);
        }

        public IDatabase RealConnection
        {
            get { return _connection; }
        }

        public void Open()
        { }

        public void Close()
        {
            _redisMultiplexer.Close();
        }

        public bool Expiry(string key, int timeInSeconds)
        {
            TimeSpan timeSpan = new TimeSpan(0, 0, timeInSeconds);
            RedisKey redisKey = key;
            return (bool)RetryLogic(() => _connection.KeyExpire(redisKey,timeSpan));
        }

        public object Eval(string script, string[] keyArgs, object[] valueArgs)
        {
            RedisKey[] redisKeyArgs = new RedisKey[keyArgs.Length];
            RedisValue[] redisValueArgs = new RedisValue[valueArgs.Length];

            int i = 0;
            foreach (string key in keyArgs)
            {
                redisKeyArgs[i] = key;
                i++;
            }

            i = 0;
            foreach (object val in valueArgs)
            {
                if (val.GetType() == typeof(byte[]))
                {
                    // User data is always in bytes
                    redisValueArgs[i] = (byte[])val;
                }
                else
                {
                    // Internal data like session timeout and indexes are stored as strings
                    redisValueArgs[i] = val.ToString();
                }
                i++;
            }
            return RetryLogic(() => _connection.ScriptEvaluate(script, redisKeyArgs, redisValueArgs));
        }

        private object RetryForScriptNotFound(Func<object> redisOperation)
        {
            try
            {
                return redisOperation.Invoke();
            }
            catch (Exception e)
            {
                if (e.Message.Contains("NOSCRIPT"))
                {
                    // Second call should pass if it was script not found issue
                    return redisOperation.Invoke();
                }
                throw;
            }
        }

        /// <summary>
        /// If retry timout is provide than we will retry first time after 20 ms and after that every 1 sec till retry timout is expired or we get value.
        /// </summary>
        private object RetryLogic(Func<object> redisOperation)
        {
          int timeToSleepBeforeRetryInMiliseconds = 20;
          DateTime startTime = DateTime.Now;
          while (true)
          {
            try
            {
              return RetryForScriptNotFound(redisOperation);
            }
            catch (RedisConnectionException)
            {
              _redisMultiplexer.Close();
              ConnectToRedis(_configuration);
            }
            catch (RedisServerException exception)
            {
              //if slaves are READONLY and master crash and goes up faster than we start connection to new master than connection is against READONLY slave
              // and if we try to write command fail with this exception. Than we need to find new master and retry command against this new master.
              if (exception.Message.Contains("READONLY"))
              {
                _redisMultiplexer.Close();
                ConnectToRedis(_configuration);
              }
              else
              {
                TimeSpan passedTime = DateTime.Now - startTime;
                if (_configuration.RetryTimeout < passedTime)
                {
                  throw;
                }
                timeToSleepBeforeRetryInMiliseconds = SleepBeforeRetry(passedTime, timeToSleepBeforeRetryInMiliseconds);
              }
            }
            catch (Exception)
            {
              TimeSpan passedTime = DateTime.Now - startTime;
              if (_configuration.RetryTimeout < passedTime)
              {
                throw;
              }
              timeToSleepBeforeRetryInMiliseconds = SleepBeforeRetry(passedTime, timeToSleepBeforeRetryInMiliseconds);
            }
          }
        }

        private int SleepBeforeRetry(TimeSpan passedTime, int timeToSleepBeforeRetryInMiliseconds)
        {
          var remainingTimeout = (int)(_configuration.RetryTimeout.TotalMilliseconds - passedTime.TotalMilliseconds);
          // if remaining time is less than 1 sec than wait only for that much time and than give a last try
          if (remainingTimeout < timeToSleepBeforeRetryInMiliseconds)
          {
            timeToSleepBeforeRetryInMiliseconds = remainingTimeout;
          }

          // First time try after 20 msec after that try after 1 second
          System.Threading.Thread.Sleep(timeToSleepBeforeRetryInMiliseconds);
          timeToSleepBeforeRetryInMiliseconds = 1000;
          return timeToSleepBeforeRetryInMiliseconds;
        }

        public int GetSessionTimeout(object rowDataFromRedis)
        {
            RedisResult rowDataAsRedisResult = (RedisResult)rowDataFromRedis;
            RedisResult[] lockScriptReturnValueArray = (RedisResult[])rowDataAsRedisResult;
            Debug.Assert(lockScriptReturnValueArray != null);
            Debug.Assert(lockScriptReturnValueArray[2] != null);
            int sessionTimeout = (int)lockScriptReturnValueArray[2];
            if (sessionTimeout == -1)
            {
                sessionTimeout = (int) _configuration.SessionTimeout.TotalSeconds;
            }
            // converting seconds to minutes
            sessionTimeout = sessionTimeout / 60;
            return sessionTimeout;
        }

        public bool IsLocked(object rowDataFromRedis)
        {
            RedisResult rowDataAsRedisResult = (RedisResult)rowDataFromRedis;
            RedisResult[] lockScriptReturnValueArray = (RedisResult[])rowDataAsRedisResult;
            Debug.Assert(lockScriptReturnValueArray != null);
            Debug.Assert(lockScriptReturnValueArray[3] != null);
            return (bool)lockScriptReturnValueArray[3];
        }

        public string GetLockId(object rowDataFromRedis)
        {
            return GetLockIdStatic(rowDataFromRedis);
        }

        internal static string GetLockIdStatic(object rowDataFromRedis)
        {
            RedisResult rowDataAsRedisResult = (RedisResult)rowDataFromRedis;
            RedisResult[] lockScriptReturnValueArray = (RedisResult[])rowDataAsRedisResult;
            Debug.Assert(lockScriptReturnValueArray != null);
            return (string)lockScriptReturnValueArray[0];
        }

        public ISessionStateItemCollection GetSessionData(object rowDataFromRedis)
        {
            return GetSessionDataStatic(rowDataFromRedis);
        }

        internal static ISessionStateItemCollection GetSessionDataStatic(object rowDataFromRedis)
        {
            RedisResult rowDataAsRedisResult = (RedisResult)rowDataFromRedis;
            RedisResult[] lockScriptReturnValueArray = (RedisResult[])rowDataAsRedisResult;
            Debug.Assert(lockScriptReturnValueArray != null);

            ChangeTrackingSessionStateItemCollection sessionData = null;
            if (lockScriptReturnValueArray.Length > 1 && lockScriptReturnValueArray[1] != null)
            {
                RedisResult[] data = (RedisResult[])lockScriptReturnValueArray[1];

                // LUA script returns data as object array so keys and values are store one after another
                // This list has to be even because it contains pair of <key, value> as {key, value, key, value}
                if (data != null && data.Length != 0 && data.Length % 2 == 0)
                {
                    sessionData = new ChangeTrackingSessionStateItemCollection(redisUtility);
                    // In every cycle of loop we are getting one pair of key value and putting it into session items
                    // thats why increment is by 2 because we want to move to next pair
                    for (int i = 0; (i + 1) < data.Length; i += 2)
                    {
                        string key = (string) data[i];
                        if (key != null)
                        {
                            sessionData.SetData(key, (byte[])data[i + 1]);
                        }
                    }
                }
            }
            return sessionData;
        }

        public void Set(string key, byte[] data, DateTime utcExpiry)
        {
            RedisKey redisKey = key;
            RedisValue redisValue = data;
            TimeSpan timeSpanForExpiry = utcExpiry - DateTime.UtcNow;
            _connection.StringSet(redisKey, redisValue, timeSpanForExpiry);
        }

        public byte[] Get(string key)
        {
            RedisKey redisKey = key;
            RedisValue redisValue = _connection.StringGet(redisKey);
            return redisValue;
        }

        public void Remove(string key)
        {
            RedisKey redisKey = key;
            _connection.KeyDelete(redisKey);
        }

        public byte[] GetOutputCacheDataFromResult(object rowDataFromRedis)
        {
            RedisResult rowDataAsRedisResult = (RedisResult)rowDataFromRedis;
            return (byte[]) rowDataAsRedisResult;
        }
    }
}
