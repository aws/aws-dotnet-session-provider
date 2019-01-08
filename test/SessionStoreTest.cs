using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.SessionProvider;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.SessionState;

namespace AWS.SessionProvider.Test
{
    [TestClass]
    public class SessionStoreTest
    {
        private static DynamoDBSessionStateStore store;
        private string tableName;
        private const string ttlAttributeName = "TTL";
        private readonly int ttlExpiredSessionsSeconds = (int)TimeSpan.FromDays(7).TotalSeconds;
        private static string sessionId = DateTime.Now.ToFileTime().ToString();
        private static TimeSpan newTimeout = TimeSpan.FromSeconds(5);
        private static FieldInfo timeoutField;
        private static TimeSpan waitPeriod = TimeSpan.FromSeconds(10);
        private static TimeSpan tableActiveMaxTime = TimeSpan.FromMinutes(5);
        private static RegionEndpoint region = RegionEndpoint.USEast1;

        private static AmazonDynamoDBClient CreateClient()
        {
            var client = new AmazonDynamoDBClient(region);
            return client;
        }

        [TestInitialize]
        public void Init()
        {
            timeoutField = typeof(DynamoDBSessionStateStore).GetField("_timeout", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(timeoutField);
        }

        [TestCleanup]
        public void Cleanup()
        {
            using (var client = CreateClient())
            {
                client.DeleteTable(new DeleteTableRequest
                {
                    TableName = this.tableName
                });
                WaitUntilTableReady(client, null);
            }
        }

        [TestMethod]
        public void DynamoDBSessionStateStoreTest()
        {
            var config = new NameValueCollection();
            config.Add(DynamoDBSessionStateStore.CONFIG_REGION, region.SystemName);
            config.Add(DynamoDBSessionStateStore.CONFIG_TABLE, "SessionStoreWithUserSpecifiedCapacity");
            config.Add(DynamoDBSessionStateStore.CONFIG_APPLICATION, "IntegTest");
            config.Add(DynamoDBSessionStateStore.CONFIG_INITIAL_READ_UNITS, "10");
            config.Add(DynamoDBSessionStateStore.CONFIG_INITIAL_WRITE_UNITS, "10");
            config.Add(DynamoDBSessionStateStore.CONFIG_CREATE_TABLE_IF_NOT_EXIST, "true");
            Test(config);

            config.Add(DynamoDBSessionStateStore.CONFIG_TTL_ATTRIBUTE, ttlAttributeName);
            config.Add(DynamoDBSessionStateStore.CONFIG_TTL_EXPIRED_SESSIONS_SECONDS, ttlExpiredSessionsSeconds.ToString());
            Test(config);
        }

        [TestMethod]
        public void DynamoDBOnDemandCapacityTest()
        {
            var config = new NameValueCollection();
            config.Add(DynamoDBSessionStateStore.CONFIG_REGION, region.SystemName);
            config.Add(DynamoDBSessionStateStore.CONFIG_TABLE, "SessionStoreWithOnDemandCapacity");
            config.Add(DynamoDBSessionStateStore.CONFIG_APPLICATION, "IntegTest2");
            config.Add(DynamoDBSessionStateStore.CONFIG_ON_DEMAND_READ_WRITE_CAPACITY, "true");
            config.Add(DynamoDBSessionStateStore.CONFIG_CREATE_TABLE_IF_NOT_EXIST, "true");
            Test(config);
        }

        private void Test(NameValueCollection config)
        {
            using (var client = CreateClient())
            {
                store = new DynamoDBSessionStateStore("TestSessionProvider", config);
                timeoutField.SetValue(store, newTimeout);

                this.tableName = config[DynamoDBSessionStateStore.CONFIG_TABLE];

                WaitUntilTableReady(client, TableStatus.ACTIVE);
                var table = Table.LoadTable(client, this.tableName);

                var creationTime = DateTime.Now;
                store.CreateUninitializedItem(null, sessionId, 10);
                var items = GetAllItems(table);
                Assert.AreEqual(1, items.Count);
                var testTtl = config.AllKeys.Contains(DynamoDBSessionStateStore.CONFIG_TTL_ATTRIBUTE);
                var firstItem =items[0];
                Assert.AreEqual(testTtl, firstItem.ContainsKey(ttlAttributeName));
                if (testTtl)
                {
                    var epochSeconds = firstItem[ttlAttributeName].AsInt();
                    Assert.AreNotEqual(0, epochSeconds);
                    var expiresDateTime = AWSSDKUtils.ConvertFromUnixEpochSeconds(epochSeconds);
                    var expectedExpiresDateTime = (creationTime + newTimeout).AddSeconds(ttlExpiredSessionsSeconds);
                    Assert.IsTrue((expiresDateTime - expectedExpiresDateTime) < TimeSpan.FromMinutes(1));
                }

                var hasOnDemandReadWriteCapacity = config.AllKeys.Contains(DynamoDBSessionStateStore.CONFIG_ON_DEMAND_READ_WRITE_CAPACITY);
                if (hasOnDemandReadWriteCapacity)
                {
                    var mode = client.DescribeTable(tableName).Table.BillingModeSummary.BillingMode;
                    Assert.AreEqual(mode, BillingMode.PAY_PER_REQUEST);
                }

                bool locked;
                TimeSpan lockAge;
                object lockId;
                SessionStateActions actionFlags;
                store.GetItem(null, sessionId, out locked, out lockAge, out lockId, out actionFlags);

                Thread.Sleep(newTimeout);

                DynamoDBSessionStateStore.DeleteExpiredSessions(client, tableName);
                items = GetAllItems(table);
                Assert.AreEqual(0, items.Count);
            }
        }

        private static List<Document> GetAllItems(Table table)
        {
            Assert.IsNotNull(table);

            var allItems = table.Scan(new ScanFilter()).GetRemaining().ToList();
            return allItems;
        }
        private void WaitUntilTableReady(AmazonDynamoDBClient client, TableStatus targetStatus)
        {
            var startTime = DateTime.Now;
            TableStatus status;
            while ((DateTime.Now - startTime) < tableActiveMaxTime)
            {
                try
                {
                    status = client.DescribeTable(tableName).Table.TableStatus;
                }
                catch(ResourceNotFoundException)
                {
                    status = null;
                }

                if (status == targetStatus)
                    return;
                Thread.Sleep(waitPeriod);
            }
        }
    }
}
