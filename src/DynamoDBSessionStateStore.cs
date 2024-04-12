﻿/*
 * Copyright 2012-2013 Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
using System;
using System.IO;
using System.Web;
using System.Web.Configuration;
using System.Configuration;
using System.Configuration.Provider;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Web.SessionState;
using System.Text;
using System.Threading;

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Runtime;
using Amazon.Runtime.Internal.Util;
using Amazon.Util;
using System.Reflection;

namespace Amazon.SessionProvider
{
    /// <summary>
    /// DynamoDBSessionStateStore is a custom session state provider that can be used inside of an ASP.NET application. Session state is saved 
    /// inside a DynamoDB table that can be configured in the web.config. If the table does not exist the provider will create
    /// it during initialization with default read and write units set to 10 and 5 unless configured otherwise. If the table is created 
    /// the application startup will block for about a minute while the table is being created.
    /// 
    /// Example web.config entry setting up the session state provider.
    /// <code>
    /// &lt;sessionState 
    ///   mode="Custom" 
    ///   customProvider="DynamoDBSessionStoreProvider"&gt;
    ///   &lt;providers&gt;
    ///     &lt;add name="DynamoDBSessionStoreProvider" 
    ///          type="Amazon.SessionProvider.DynamoDBSessionStateStore, AWS.SessionProvider"
    ///          AWSProfileName="default"
    ///          AWSProfilesLocation=".aws/credentials"
    ///          Region="us-east-1"
    ///          Table="ASP.NET_SessionState"
    ///          TTLAttributeName="ExpirationTime"
    ///          TTLExpiredSessionsSeconds="86400"
    ///          /&gt;
    ///   &lt;/providers&gt;
    /// &lt;/sessionState&gt;
    /// </code>
    /// 
    /// <para>
    /// The schema for the table used to store session requires a string hash key with no range key. The provider will look up the name of the hash key during 
    /// initialization so any name can be given for the hash key.
    /// </para>
    /// 
    /// <para>
    /// Below is a list of configuration attributes that can specified in the provider element in the web.config.
    /// <list type="table">
    ///     <listheader>
    ///         <term>Config Constant</term>
    ///         <term>Use</term>
    ///     </listheader>
    ///     <item>
    ///         <term>AWSProfileName</term>
    ///         <description>Profile used. This can be set at either the provider or in the appSettings.</description>
    ///     </item>
    ///     <item>
    ///         <term>AWSProfilesLocation</term>
    ///         <description>Location of the credentials file. This can be set at either the provider or in the appSettings.</description>
    ///     </item>
    ///     <item>
    ///         <term>Region</term>
    ///         <description>Required string attribute. The region to use DynamoDB in. Possible values are us-east-1, us-west-1, us-west-2, eu-west-1, ap-northeast-1, ap-southeast-1.</description>
    ///     </item>
    ///     <item>
    ///         <term>Service URL</term>
    ///         <description>The URL of the DynamoDB endpoint. This can be used instead of region. This property is commonly used for connecting to DynamoDB Local (e.g. http://localhost:8000/)</description>
    ///     </item>
    ///     <item>
    ///         <term>Application</term>
    ///         <description>Optional string attribute. Application is used to partition the session data in the table so it can be used for more than one application.</description>
    ///     </item>
    ///     <item>
    ///         <term>Table</term>
    ///         <description>Optional string attribute. The table used to store session data. The default is ASP.NET_SessionState.</description>
    ///     </item>
    ///     <item>
    ///         <term>ReadCapacityUnits</term>
    ///         <description>Optional int attribute. The read capacity units if the table is created. The default is 10.</description>
    ///     </item>
    ///     <item>
    ///         <term>WriteCapacityUnits</term>
    ///         <description>Optional int attribute. The write capacity units if the table is created. The default is 5.</description>
    ///     </item>
    ///     <item>
    ///         <term>UseOnDemandReadWriteCapacity</term>
    ///         <description>Optional boolean attribute. UseOnDemandReadWriteCapacity controls whether the table will be created with its read/write capacity set to On-Demand. Default is false.</description>
    ///     </item>
    ///     <item>
    ///         <term>CreateIfNotExist</term>
    ///         <description>Optional boolean attribute. CreateIfNotExist controls whether the table will be auto created if it doesn't exist. Default is true.</description>
    ///     </item>
    ///     <item>
    ///         <term>StrictDisableSession</term>
    ///         <description>Optional boolean attribute. If EnabledSessionState is False globally or on an individual page/view/controller, ASP.NET will still send a keepalive request to dynamo. Setting this to true disables keepalive requests when EnableSessionState is False. Default is false.</description>
    ///     </item>
    ///     <item>
    ///         <term>TTLAttributeName</term>
    ///         <description>Optional string attribute. The name of the TTL attribute for the table. This must be specified for session items to contain TTL-compatible data.</description>
    ///     </item>
    ///     <item>
    ///         <term>TTLExpiredSessionsSeconds</term>
    ///         <description>Optional int attribute. The minimum number of seconds after session expiration before sessions are eligible for TTL. By default this is 0. This value must be non-negative.</description>
    ///     </item>
    /// </list>
    /// </para>
    /// </summary>
    public class DynamoDBSessionStateStore : SessionStateStoreProviderBase
    {
        private const string CURRENT_RECORD_FORMAT_VERSION = "1";
        private const int DESCRIBE_INTERVAL = 5000;
        private const string ACTIVE_STATUS = "Active";

        private static readonly GetItemOperationConfig CONSISTENT_READ_GET = new GetItemOperationConfig();
        private static readonly UpdateItemOperationConfig LOCK_UPDATE_CONFIG = new UpdateItemOperationConfig();

        private static readonly ILogger _logger = Logger.GetLogger(typeof(DynamoDBSessionStateStore));

        private static readonly string _assemblyFileVersion = GetAssemblyFileVersion();
        private static readonly string _userAgentSuffix = $"lib/SessionStateProvider#{_assemblyFileVersion}";

        static DynamoDBSessionStateStore()
        {
            CONSISTENT_READ_GET.ConsistentRead = true;

            LOCK_UPDATE_CONFIG.Expected = new Document();
            LOCK_UPDATE_CONFIG.Expected[ATTRIBUTE_LOCKED] = false;
            LOCK_UPDATE_CONFIG.ReturnValues = ReturnValues.AllNewAttributes;
        }

        // Possible config names set in the web.config.
        public const string CONFIG_ACCESSKEY = "AWSAccessKey";
        public const string CONFIG_SECRETKEY = "AWSSecretKey";
        public const string CONFIG_PROFILENAME = "AWSProfileName";
        public const string CONFIG_PROFILESLOCATION = "AWSProfilesLocation";
        public const string CONFIG_APPLICATION = "Application";
        public const string CONFIG_TABLE = "Table";
        public const string CONFIG_REGION = "Region";
        public const string CONFIG_SERVICE_URL = "ServiceURL";
        public const string CONFIG_INITIAL_READ_UNITS = "ReadCapacityUnits";
        public const string CONFIG_INITIAL_WRITE_UNITS = "WriteCapacityUnits";
        public const string CONFIG_ON_DEMAND_READ_WRITE_CAPACITY = "UseOnDemandReadWriteCapacity";
        public const string CONFIG_CREATE_TABLE_IF_NOT_EXIST = "CreateIfNotExist";
        public const string CONFIG_STRICT_DISABLE_SESSION = "StrictDisableSession";
        public const string CONFIG_TTL_ATTRIBUTE = "TTLAttributeName";
        public const string CONFIG_TTL_EXPIRED_SESSIONS_SECONDS = "TTLExpiredSessionsSeconds";

        // This is not const because we will use whatever is the hash key defined for 
        // the table as long as it is a string.
        private static string ATTRIBUTE_SESSION_ID = "SessionId";

        // The attribute names stored for the session record.
        public const string ATTRIBUTE_CREATE_DATE = "CreateDate";
        public const string ATTRIBUTE_LOCKED = "Locked";
        public const string ATTRIBUTE_LOCK_DATE = "LockDate";
        public const string ATTRIBUTE_LOCK_ID = "LockId";
        public const string ATTRIBUTE_EXPIRES = "Expires";
        public const string ATTRIBUTE_SESSION_ITEMS = "SessionItems";
        public const string ATTRIBUTE_FLAGS = "Flags";
        public const string ATTRIBUTE_RECORD_FORMAT_VERSION = "Ver";

        const string DEFAULT_TABLENAME = "ASP.NET_SessionState";

        // Fields that come from the web.config
        string _accessKey;
        string _secretKey;
        string _profileName;
        string _profilesLocation;
        string _tableName = DEFAULT_TABLENAME;
        string _regionName;
        string _serviceURL;
        string _application = "";
        int _initialReadUnits = 10;
        int _initialWriteUnits = 5;
        bool _useOnDemandReadWriteCapacity = false;
        bool _createIfNotExist = true;
        bool _strictDisableSession = false;
        uint _ttlExtraSeconds = 0;
        string _ttlAttributeName = null;

        IAmazonDynamoDB _ddbClient;
        Table _table;
        TimeSpan _timeout = new TimeSpan(0, 20, 0);

        /// <summary>
        /// Default Constructor
        /// </summary>
        public DynamoDBSessionStateStore()
        {
        }

        /// <summary>
        /// Constructor for testing.
        /// </summary>
        /// <param name="ddbClient"></param>
        public DynamoDBSessionStateStore(IAmazonDynamoDB ddbClient)
        {
            this._ddbClient = ddbClient;
            SetupTable();
        }

        /// <summary>
        /// Constructor for testing.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="config"></param>
        public DynamoDBSessionStateStore(string name, NameValueCollection config)
        {
            Initialize(name, config);
        }

        /// <summary>
        /// Gets the name of the table used to store session data.
        /// </summary>
        public string TableName
        {
            get { return this._tableName; }
        }

        /// <summary>
        /// Initializes the provider by pulling the config info from the web.config and validate/create the DynamoDB table.
        /// If the table is being created this method will block until the table is active.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="config"></param>
        public override void Initialize(string name, NameValueCollection config)
        {
            _logger.InfoFormat("Initialize : Initializing Session provider {0}", name);

            if (config == null)
                throw new ArgumentNullException("config");

            base.Initialize(name, config);

            GetConfigSettings(config);


            RegionEndpoint region = null;
            if(!string.IsNullOrEmpty(this._regionName))
                region = RegionEndpoint.GetBySystemName(this._regionName);

            AWSCredentials credentials = null;
            if (!string.IsNullOrEmpty(this._accessKey))
            {
                credentials = new BasicAWSCredentials(this._accessKey, this._secretKey);
            }
            else if (!string.IsNullOrEmpty(this._profileName))
            {
                if (string.IsNullOrEmpty(this._profilesLocation))
                    credentials = new StoredProfileAWSCredentials(this._profileName);
                else
                    credentials = new StoredProfileAWSCredentials(this._profileName, this._profilesLocation);
            }

            AmazonDynamoDBConfig ddbConfig = new AmazonDynamoDBConfig();

            if (region != null)
                ddbConfig.RegionEndpoint = region;
            if (!string.IsNullOrEmpty(this._serviceURL))
                ddbConfig.ServiceURL = this._serviceURL;

            if (credentials != null)
            {
                this._ddbClient = new AmazonDynamoDBClient(credentials, ddbConfig);
            }
            else
            {
                this._ddbClient = new AmazonDynamoDBClient(ddbConfig);
            }

            ((AmazonDynamoDBClient)this._ddbClient).BeforeRequestEvent += DynamoDBSessionStateStore_BeforeRequestEvent;

            SetupTable();
        }

        const string UserAgentHeader = "User-Agent";
        void DynamoDBSessionStateStore_BeforeRequestEvent(object sender, RequestEventArgs e)
        {
            Amazon.Runtime.WebServiceRequestEventArgs args = e as Amazon.Runtime.WebServiceRequestEventArgs;
            if (args == null || !args.Headers.ContainsKey(UserAgentHeader) || args.Headers[UserAgentHeader].Contains(_userAgentSuffix))
                return;

            args.Headers[UserAgentHeader] = args.Headers[UserAgentHeader] + " " + _userAgentSuffix;
        }

        private static string GetAssemblyFileVersion()
        {
            var assembly = typeof(DynamoDBSessionStateStore).GetTypeInfo().Assembly;
            AssemblyFileVersionAttribute attribute = assembly.GetCustomAttribute(typeof(AssemblyFileVersionAttribute)) as AssemblyFileVersionAttribute;

            var version = attribute == null ? "Unknown" : attribute.Version;
            return version;
        }

        private void SetupTable()
        {
            try
            {
                var tableConfig = CreateTableConfig();
                this._table = Table.LoadTable(this._ddbClient, tableConfig);
            }
            catch (ResourceNotFoundException) { }

            if (this._table == null)
            {
                if (this._createIfNotExist)
                    this._table = CreateTable();
                else
                    throw new AmazonDynamoDBException(string.Format("Table {0} was not found to be used to store session state and autocreate is turned off.", this._tableName));
            }
            else
            {
                ValidateTable();
            }
        }

        private void GetConfigSettings(NameValueCollection config)
        {
            this._accessKey = config[CONFIG_ACCESSKEY];
            this._secretKey = config[CONFIG_SECRETKEY];
            this._profileName= config[CONFIG_PROFILENAME];
            this._profilesLocation = config[CONFIG_PROFILESLOCATION];
            this._regionName = config[CONFIG_REGION];
            this._serviceURL = config[CONFIG_SERVICE_URL];

            if (!string.IsNullOrEmpty(config[CONFIG_TABLE]))
            {
                this._tableName = config[CONFIG_TABLE];
            }

            if (!string.IsNullOrEmpty(config[CONFIG_APPLICATION]))
            {
                this._application = config[CONFIG_APPLICATION];
            }

            if (!string.IsNullOrEmpty(config[CONFIG_CREATE_TABLE_IF_NOT_EXIST]))
            {
                this._createIfNotExist = bool.Parse(config[CONFIG_CREATE_TABLE_IF_NOT_EXIST]);
            }

            if (!string.IsNullOrEmpty(config[CONFIG_INITIAL_READ_UNITS]))
            {
                this._initialReadUnits = int.Parse(config[CONFIG_INITIAL_READ_UNITS]);
            }

            if (!string.IsNullOrEmpty(config[CONFIG_INITIAL_WRITE_UNITS]))
            {
                this._initialWriteUnits = int.Parse(config[CONFIG_INITIAL_WRITE_UNITS]);
            }

            if (!string.IsNullOrEmpty(config[CONFIG_ON_DEMAND_READ_WRITE_CAPACITY]))
            {
                this._useOnDemandReadWriteCapacity = bool.Parse(config[CONFIG_ON_DEMAND_READ_WRITE_CAPACITY]);
            }

            if (!string.IsNullOrEmpty(config[CONFIG_STRICT_DISABLE_SESSION]))
            {
                this._strictDisableSession = bool.Parse(config[CONFIG_STRICT_DISABLE_SESSION]);
            }

            if (!string.IsNullOrEmpty(config[CONFIG_TTL_ATTRIBUTE]))
            {
                this._ttlAttributeName = config[CONFIG_TTL_ATTRIBUTE];
            }

            if (!string.IsNullOrEmpty(config[CONFIG_TTL_EXPIRED_SESSIONS_SECONDS]))
            {
                this._ttlExtraSeconds = uint.Parse(config[CONFIG_TTL_EXPIRED_SESSIONS_SECONDS]);
            }

            string applicationName = System.Web.Hosting.HostingEnvironment.ApplicationVirtualPath;
            if (applicationName != null)
            {
                Configuration cfg = WebConfigurationManager.OpenWebConfiguration(applicationName);
                if (cfg != null)
                {
                    SessionStateSection sessionConfig = cfg.GetSection("system.web/sessionState") as SessionStateSection;
                    if (sessionConfig != null)
                    {
                        this._timeout = sessionConfig.Timeout;
                    }
                }
            }
        }

        /// <summary>
        /// Provider returns false for this method.
        /// </summary>
        /// <param name="expireCallback"></param>
        /// <returns></returns>
        public override bool SetItemExpireCallback(SessionStateItemExpireCallback expireCallback)
        {
            return false;
        }

        /// <summary>
        /// Returns read-only session-state data from the DynamoDB table.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="sessionId"></param>
        /// <param name="locked"></param>
        /// <param name="lockAge"></param>
        /// <param name="lockId"></param>
        /// <param name="actionFlags"></param>
        /// <returns></returns>
        public override SessionStateStoreData GetItem(HttpContext context,
                                                      string sessionId,
                                                      out bool locked,
                                                      out TimeSpan lockAge,
                                                      out object lockId,
                                                      out SessionStateActions actionFlags)
        {
            LogInfo("GetItem", sessionId, context);

            return GetSessionStoreItem(false, context, sessionId, out locked,
              out lockAge, out lockId, out actionFlags);
        }

        /// <summary>
        /// Returns session-state data from the DynamoDB table.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="sessionId"></param>
        /// <param name="locked"></param>
        /// <param name="lockAge"></param>
        /// <param name="lockId"></param>
        /// <param name="actionFlags"></param>
        /// <returns></returns>
        public override SessionStateStoreData GetItemExclusive(HttpContext context,
                                                              string sessionId,
                                                              out bool locked,
                                                              out TimeSpan lockAge,
                                                              out object lockId,
                                                              out SessionStateActions actionFlags)
        {
            LogInfo("GetItemExclusive", sessionId, context);
            return GetSessionStoreItem(true, context, sessionId, out locked,
              out lockAge, out lockId, out actionFlags);
        }

        /// <summary>
        /// Get the session for DynamoDB and optionally lock the record.
        /// </summary>
        /// <param name="lockRecord"></param>
        /// <param name="context"></param>
        /// <param name="sessionId"></param>
        /// <param name="locked"></param>
        /// <param name="lockAge"></param>
        /// <param name="lockId"></param>
        /// <param name="actionFlags"></param>
        /// <returns></returns>
        private SessionStateStoreData GetSessionStoreItem(bool lockRecord,
                                                          HttpContext context,
                                                          string sessionId,
                                                          out bool locked,
                                                          out TimeSpan lockAge,
                                                          out object lockId,
                                                          out SessionStateActions actionFlags)
        {
            LogInfo("GetSessionStoreItem", sessionId, lockRecord, context);

            // Initial values for return value and out parameters.
            SessionStateStoreData item = null;
            lockAge = TimeSpan.Zero;
            lockId = Guid.NewGuid().ToString();
            locked = false;
            actionFlags = SessionStateActions.None;

            bool foundRecord = false;
            bool deleteData = false;

            DateTime newLockedDate = DateTime.Now;


            Document session = null;
            if (lockRecord)
            {
                Document lockDoc = new Document();
                lockDoc[ATTRIBUTE_SESSION_ID] = GetHashKey(sessionId);
                lockDoc[ATTRIBUTE_LOCK_ID] = lockId.ToString();
                lockDoc[ATTRIBUTE_LOCKED] = true;
                lockDoc[ATTRIBUTE_LOCK_DATE] = DateTime.Now;

                try
                {
                    session = this._table.UpdateItem(lockDoc, LOCK_UPDATE_CONFIG);
                    locked = false;
                }
                catch (ConditionalCheckFailedException)
                {
                    // This means the record is already locked by another request.
                    locked = true;
                }
            }

            if (session == null)
            {
                session = this._table.GetItem(GetHashKey(sessionId), CONSISTENT_READ_GET);
                if (session == null && lockRecord)
                {
                    locked = true;
                }
            }

            string serializedItems = null;
            if (session != null)
            {
                DateTime expire = (DateTime)session[ATTRIBUTE_EXPIRES];

                if (expire < DateTime.Now)
                {
                    deleteData = true;
                    locked = false;
                }
                else
                {
                    foundRecord = true;

                    DynamoDBEntry entry;
                    if (session.TryGetValue(ATTRIBUTE_SESSION_ITEMS, out entry))
                    {
                        serializedItems = (string)entry;
                    }

                    if (session.Contains(ATTRIBUTE_LOCK_ID))
                        lockId = (string)session[ATTRIBUTE_LOCK_ID];


                    if (session.Contains(ATTRIBUTE_FLAGS))
                        actionFlags = (SessionStateActions)((int)session[ATTRIBUTE_FLAGS]);

                    if (session.Contains(ATTRIBUTE_LOCK_DATE) && session[ATTRIBUTE_LOCK_DATE] != null)
                    {
                        DateTime lockDate = (DateTime)session[ATTRIBUTE_LOCK_DATE];
                        lockAge = DateTime.Now.Subtract(lockDate);
                    }
                }
            }

            if (deleteData)
            {
                this.deleteItem(sessionId);
            }

            // The record was not found. Ensure that locked is false.
            if (!foundRecord)
            {
                locked = false;
                lockId = null;
            }

            // If the record was found and you obtained a lock, then clear the actionFlags,
            // and create the SessionStateStoreItem to return.
            if (foundRecord && !locked)
            {
                if (actionFlags == SessionStateActions.InitializeItem)
                {
                    Document updateDoc = new Document();
                    updateDoc[ATTRIBUTE_SESSION_ID] = GetHashKey(sessionId);
                    updateDoc[ATTRIBUTE_FLAGS] = 0;
                    this._table.UpdateItem(updateDoc);

                    item = CreateNewStoreData(context, (int)this._timeout.TotalMinutes);
                }
                else
                {
                    item = deserialize(context, serializedItems, (int)this._timeout.TotalMinutes);
                }
            }

            return item;
        }

        /// <summary>
        /// Updates the session-item information in the session-state data store with values from the current request, and clears the lock on the data.
        /// </summary>
        /// <param name="context">The HttpContext for the current request.</param>
        /// <param name="sessionId">The session identifier for the current request.</param>
        /// <param name="item">The SessionStateStoreData object that contains the current session values to be stored.</param>
        /// <param name="lockId">The lock identifier for the current request.</param>
        /// <param name="newItem">true to identify the session item as a new item; false to identify the session item as an existing item.</param>
        public override void SetAndReleaseItemExclusive(HttpContext context,
                                                         string sessionId,
                                                         SessionStateStoreData item,
                                                         object lockId,
                                                         bool newItem)
        {
            LogInfo("SetAndReleaseItemExclusive", sessionId, lockId, newItem, context);

            string serialized = serialize(item.Items as SessionStateItemCollection);
            var expiration = DateTime.Now.Add(this._timeout);

            Document newValues = new Document();
            newValues[ATTRIBUTE_SESSION_ID] = GetHashKey(sessionId);
            newValues[ATTRIBUTE_LOCKED] = false;
            newValues[ATTRIBUTE_LOCK_ID] = null;
            newValues[ATTRIBUTE_LOCK_DATE] = DateTime.Now;
            newValues[ATTRIBUTE_EXPIRES] = expiration;
            newValues[ATTRIBUTE_FLAGS] = 0;
            newValues[ATTRIBUTE_SESSION_ITEMS] = serialized;
            newValues[ATTRIBUTE_RECORD_FORMAT_VERSION] = CURRENT_RECORD_FORMAT_VERSION;
            SetTTLAttribute(newValues, expiration);

            if (newItem)
            {
                newValues[ATTRIBUTE_CREATE_DATE] = DateTime.Now;
                this._table.PutItem(newValues);
            }
            else
            {
                Document expected = new Document();
                expected[ATTRIBUTE_LOCK_ID] = lockId.ToString();

                // Not really any reason the condition should fail unless we get in some sort of weird
                // app pool reset mode.
                try
                {
                    this._table.UpdateItem(newValues, new UpdateItemOperationConfig() { Expected = expected });
                }
                catch (ConditionalCheckFailedException) { LogInfo("(SetAndReleaseItemExclusive) Conditional check failed for update.", sessionId, context); }
            }
        }

        /// <summary>
        /// Releases a lock on an item in the session data store.
        /// </summary>
        /// <param name="context">The HttpContext for the current request.</param>
        /// <param name="sessionId">The session identifier for the current request.</param>
        /// <param name="lockId">The lock identifier for the current request.</param>
        public override void ReleaseItemExclusive(HttpContext context, string sessionId, object lockId)
        {
            LogInfo("ReleaseItemExclusive", sessionId, lockId, context);

            Document doc = this._table.GetItem(GetHashKey(sessionId), CONSISTENT_READ_GET);
            if (doc == null)
            {
                LogError("ReleaseItemExclusive Failed to retrieve state for session id: " + sessionId, sessionId, lockId, context);
                return;
            }

            var expiration = DateTime.Now.Add(this._timeout);
            doc[ATTRIBUTE_LOCKED] = false;
            doc[ATTRIBUTE_EXPIRES] = expiration;
            SetTTLAttribute(doc, expiration);

            Document expected = new Document();
            expected[ATTRIBUTE_LOCK_ID] = lockId.ToString();

            try
            {
                this._table.UpdateItem(doc, new UpdateItemOperationConfig() { Expected = expected });
            }
            catch (ConditionalCheckFailedException) { LogInfo("(ReleaseItemExclusive) Conditional check failed for update.", sessionId, context); }
        }

        /// <summary>
        /// Removes the session record for DynamoDB.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="sessionId"></param>
        /// <param name="lockId"></param>
        /// <param name="item"></param>
        public override void RemoveItem(HttpContext context, string sessionId, object lockId, SessionStateStoreData item)
        {
            LogInfo("RemoveItem", sessionId, lockId, context);

            if (lockId == null)
            {
                deleteItem(sessionId);
            }
            else
            {
                Document doc = this._table.GetItem(GetHashKey(sessionId), CONSISTENT_READ_GET);
                if (doc.Contains(ATTRIBUTE_LOCK_ID))
                {
                    string currentLockId = (string)doc[ATTRIBUTE_LOCK_ID];
                    if (string.Equals(currentLockId, lockId))
                    {
                        deleteItem(sessionId);
                    }
                }
            }
        }

        /// <summary>
        /// Creates an initial session record in the DynamoDB table.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="sessionId"></param>
        /// <param name="timeout"></param>
        public override void CreateUninitializedItem(HttpContext context, string sessionId, int timeout)
        {
            LogInfo("CreateUninitializedItem", sessionId, timeout, context);

            var expiration = DateTime.Now.Add(this._timeout);
            Document session = new Document();
            session[ATTRIBUTE_SESSION_ID] = GetHashKey(sessionId);
            session[ATTRIBUTE_LOCKED] = false;
            session[ATTRIBUTE_CREATE_DATE] = DateTime.Now;
            session[ATTRIBUTE_EXPIRES] = expiration;
            session[ATTRIBUTE_FLAGS] = 1;
            session[ATTRIBUTE_RECORD_FORMAT_VERSION] = CURRENT_RECORD_FORMAT_VERSION;
            SetTTLAttribute(session, expiration);
            this._table.PutItem(session);
        }

        /// <summary>
        /// Creates a new SessionStateStoreData object to be used for the current request.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public override SessionStateStoreData CreateNewStoreData(HttpContext context, int timeout)
        {
            LogInfo("CreateNewStoreData", timeout, context);

            HttpStaticObjectsCollection sessionStatics = null;
            if (context != null)
                sessionStatics = SessionStateUtility.GetSessionStaticObjects(context);
            return new SessionStateStoreData(new SessionStateItemCollection(), sessionStatics, timeout);
        }

        /// <summary>
        /// Updates the expiration date and time of an item in the DynamoDB table.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="sessionId"></param>
        public override void ResetItemTimeout(HttpContext context, string sessionId)
        {
            LogInfo("ResetItemTimeout", sessionId, context);

            var suppressKeepalive = _strictDisableSession && context.Session == null;
            if (suppressKeepalive)
                return;

            var expiration = DateTime.Now.Add(this._timeout);
            Document doc = new Document();
            doc[ATTRIBUTE_SESSION_ID] = GetHashKey(sessionId);
            doc[ATTRIBUTE_LOCKED] = false;
            doc[ATTRIBUTE_EXPIRES] = expiration;
            SetTTLAttribute(doc, expiration);
            this._table.UpdateItem(doc);
        }

        /// <summary>
        /// A utility method for cleaning up expired sessions that IIS failed to delete. The method performs a scan on the ASP.NET_SessionState table
        /// with a condition that the expiration date is in the past and calls delete on all the keys returned. Scans can be costly on performance
        /// so use this method sparingly like a nightly or weekly clean job.
        /// </summary>
        /// <param name="dbClient">The AmazonDynamoDB client used to find a delete expired sessions.</param>
        public static void DeleteExpiredSessions(IAmazonDynamoDB dbClient)
        {
            LogInfo("DeleteExpiredSessions");
            DeleteExpiredSessions(dbClient, DEFAULT_TABLENAME);
        }

        /// <summary>
        /// A utility method for cleaning up expired sessions that IIS failed to delete. The method performs a scan on the table
        /// with a condition that the expiration date is in the past and calls delete on all the keys returned. Scans can be costly on performance
        /// so use this method sparingly like a nightly or weekly clean job.
        /// </summary>
        /// <param name="dbClient">The AmazonDynamoDB client used to find a delete expired sessions.</param>
        /// <param name="tableName">The table to search.</param>
        public static void DeleteExpiredSessions(IAmazonDynamoDB dbClient, string tableName)
        {
            LogInfo("DeleteExpiredSessions");
            var tableConfig = CreateTableConfig(tableName);
            Table table = Table.LoadTable(dbClient, tableConfig);


            ScanFilter filter = new ScanFilter();
            filter.AddCondition(ATTRIBUTE_EXPIRES, ScanOperator.LessThan, DateTime.Now);

            ScanOperationConfig config = new ScanOperationConfig();
            config.AttributesToGet = new List<string> { ATTRIBUTE_SESSION_ID };
            config.Select = SelectValues.SpecificAttributes;
            config.Filter = filter;

            Search search = table.Scan(config);

            do
            {
                DocumentBatchWrite batchWrite = table.CreateBatchWrite();
                List<Document> page = search.GetNextSet();
                foreach (var document in page)
                {
                    batchWrite.AddItemToDelete(document);
                }
                
                batchWrite.Execute();
            } while (!search.IsDone);
        }

        /// <summary>
        /// Empty implementation of the override.
        /// </summary>
        public override void Dispose()
        {
        }

        /// <summary>
        /// Empty implementation of the override.
        /// </summary>
        /// <param name="context"></param>
        public override void InitializeRequest(HttpContext context)
        {
        }

        /// <summary>
        /// Empty implementation of the override.
        /// </summary>
        /// <param name="context"></param>
        public override void EndRequest(HttpContext context)
        {
        }


        private Table CreateTable()
        {
            CreateTableRequest createRequest = new CreateTableRequest
            {
                TableName = this._tableName,
                KeySchema = new List<KeySchemaElement>
                {
                    new KeySchemaElement
                    {
                        AttributeName = ATTRIBUTE_SESSION_ID, KeyType = "HASH"
                    }
                },
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new AttributeDefinition
                    {
                        AttributeName = ATTRIBUTE_SESSION_ID, AttributeType = "S"
                    }
                }
            };

            if (this._useOnDemandReadWriteCapacity)
            {
                createRequest.BillingMode = BillingMode.PAY_PER_REQUEST;
            }
            else
            {
                createRequest.ProvisionedThroughput = new ProvisionedThroughput
                {
                    ReadCapacityUnits = this._initialReadUnits,
                    WriteCapacityUnits = this._initialWriteUnits
                };
            }

            CreateTableResponse response = this._ddbClient.CreateTable(createRequest);

            DescribeTableRequest descRequest = new DescribeTableRequest
            {
                TableName = this._tableName
            };

            // Wait till table is active
            bool isActive = false;
            while (!isActive)
            {
                Thread.Sleep(DESCRIBE_INTERVAL);
                DescribeTableResponse descResponse = this._ddbClient.DescribeTable(descRequest);
                string tableStatus = descResponse.Table.TableStatus;

                if (string.Equals(tableStatus, ACTIVE_STATUS, StringComparison.InvariantCultureIgnoreCase))
                    isActive = true;
            }

            if (!string.IsNullOrEmpty(this._ttlAttributeName))
            {
                this._ddbClient.UpdateTimeToLive(new UpdateTimeToLiveRequest
                {
                    TableName = this._tableName,
                    TimeToLiveSpecification = new TimeToLiveSpecification
                    {
                        AttributeName = this._ttlAttributeName,
                        Enabled = true
                    }
                });
            }

            var tableConfig = CreateTableConfig();
            Table table = Table.LoadTable(this._ddbClient, tableConfig);
            return table;
        }
        private TableConfig CreateTableConfig()
        {
            var tableConfig = CreateTableConfig(this._tableName);
            if (!string.IsNullOrEmpty(this._ttlAttributeName))
            {
                tableConfig.AttributesToStoreAsEpoch.Add(this._ttlAttributeName);
            }
            return tableConfig;
        }
        private static TableConfig CreateTableConfig(string tableName)
        {
            var tableConfig = new TableConfig(tableName)
            {
                Conversion = DynamoDBEntryConversion.V1
            };
            return tableConfig;
        }

        /// <summary>
        /// Make sure existing table is valid to be used as a session store.
        /// </summary>
        private void ValidateTable()
        {
            if (this._table.HashKeys.Count != 1)
                throw new AmazonDynamoDBException(string.Format("Table {0} cannot be used to store session data because it does not define a single hash key", this._tableName));
            string hashKey = this._table.HashKeys[0];
            KeyDescription hashKeyDescription = this._table.Keys[hashKey];
            if (hashKeyDescription.Type != DynamoDBEntryType.String)
                throw new AmazonDynamoDBException(string.Format("Table {0} cannot be used to store session data because hash key is not a string.", this._tableName));

            if (this._table.RangeKeys.Count > 0)
                throw new AmazonDynamoDBException(string.Format("Table {0} cannot be used to store session data because it contains a range key in its schema.", this._tableName));

            ATTRIBUTE_SESSION_ID = hashKey;
        }

        private void SetTTLAttribute(Document doc, DateTime expiration)
        {
            if (!string.IsNullOrEmpty(this._ttlAttributeName))
                doc[this._ttlAttributeName] = expiration.AddSeconds(_ttlExtraSeconds);
        }

        private void deleteItem(string sessionId)
        {
            Document doc = new Document();
            doc[ATTRIBUTE_SESSION_ID] = GetHashKey(sessionId);
            this._table.DeleteItem(doc);
        }

        private string serialize(SessionStateItemCollection items)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(ms);

            if (items != null)
                items.Serialize(writer);

            writer.Close();

            return Convert.ToBase64String(ms.ToArray());
        }

        private SessionStateStoreData deserialize(HttpContext context, string serializedItems, int timeout)
        {
            SessionStateItemCollection sessionItems = new SessionStateItemCollection();
            if (serializedItems != null)
            {
                MemoryStream ms =
                  new MemoryStream(Convert.FromBase64String(serializedItems));

                if (ms.Length > 0)
                {
                    BinaryReader reader = new BinaryReader(ms);
                    sessionItems = SessionStateItemCollection.Deserialize(reader);
                }
            }

            HttpStaticObjectsCollection statics = null;
            if (context != null)
                statics = SessionStateUtility.GetSessionStaticObjects(context);
            return new SessionStateStoreData(sessionItems, statics, timeout);
        }

        /// <summary>
        /// Combine application and session id for hash key.
        /// </summary>
        /// <param name="sessionId"></param>
        /// <returns></returns>
        private string GetHashKey(string sessionId)
        {
            if (string.IsNullOrEmpty(this._application))
                return sessionId;

            return string.Format("{0}-{1}", this._application, sessionId);
        }

        private static void LogInfo(string methodName)
        {
            _logger.InfoFormat("{0}", methodName);
        }

        private static void LogInfo(string methodName, string sessionId, HttpContext context)
        {
            _logger.InfoFormat("{0} : SessionId {1}, Context {2}", methodName, sessionId ?? "NULL",
                context == null ? "NULL" : "HttpContext");
        }

        private static void LogInfo(string methodName, string sessionId, bool lockRecord, HttpContext context)
        {
            _logger.InfoFormat("{0} : SessionId {1}, LockRecord {2}, Context {3} ",
                methodName, sessionId ?? "NULL", lockRecord,
                context == null ? "NULL" : "HttpContext");
        }

        private static void LogInfo(string methodName, string sessionId, object lockId, bool newItem, HttpContext context)
        {
            _logger.InfoFormat("{0} : SessionId {1}, LockId {2}, NewItem {3}, Context {4} ",
                methodName, sessionId ?? "NULL",
                lockId == null ? "NULL" : lockId.ToString(), newItem,
                context == null ? "NULL" : "HttpContext");
        }

        private static void LogInfo(string methodName, string sessionId, object lockId, HttpContext context)
        {
            _logger.InfoFormat("{0} : SessionId {1}, LockId {2}, Context {3} ", methodName, sessionId ?? "NULL",
                lockId == null ? "NULL" : lockId.ToString(),
                context == null ? "NULL" : "HttpContext");
        }

        private static void LogInfo(string methodName, string sessionId, int timeout, HttpContext context)
        {
            _logger.InfoFormat("{0} : SessionId {1}, Timeout {2}, Context {3} ", methodName, sessionId ?? "NULL",
                timeout, context == null ? "NULL" : "HttpContext");
        }

        private static void LogInfo(string methodName, int timeout, HttpContext context)
        {
            _logger.InfoFormat("{0} : Timeout {1}, Context {2} ", methodName,
                timeout, context == null ? "NULL" : "HttpContext");
        }

        private static void LogError(string methodName, string sessionId, object lockId, HttpContext context)
        {
            string message = string.Format("{0} : SessionId {1}, LockId {2}, Context {3} ", methodName, sessionId ?? "NULL",
                lockId == null ? "NULL" : lockId.ToString(),
                context == null ? "NULL" : "HttpContext");
            _logger.Error(new Exception(message), message);
        }

        private static void LogError(string methodName, Exception exception)
        {
            _logger.Error(exception, "{0} : {1}", methodName, exception.Message);
        }
    }
}
