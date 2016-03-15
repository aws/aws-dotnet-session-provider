using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.SessionProvider;
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
        private static NameValueCollection config;
        private const string tableName = "SessionStore";
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
            config = new NameValueCollection();
            config.Add(DynamoDBSessionStateStore.CONFIG_REGION, region.SystemName);
            config.Add(DynamoDBSessionStateStore.CONFIG_TABLE, tableName);
            config.Add(DynamoDBSessionStateStore.CONFIG_APPLICATION, "IntegTest");
            config.Add(DynamoDBSessionStateStore.CONFIG_INITIAL_READ_UNITS, "10");
            config.Add(DynamoDBSessionStateStore.CONFIG_INITIAL_WRITE_UNITS, "10");
            config.Add(DynamoDBSessionStateStore.CONFIG_CREATE_TABLE_IF_NOT_EXIST, "true");

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
                    TableName = tableName
                });
                WaitUntilTableReady(client, null);
            }
        }

        [TestMethod]
        public void DynamoDBSessionStateStoreTest()
        {
            using (var client = CreateClient())
            {
                store = new DynamoDBSessionStateStore("TestSessionProvider", config);
                timeoutField.SetValue(store, newTimeout);

                WaitUntilTableReady(client, TableStatus.ACTIVE);
                var table = Table.LoadTable(client, tableName);

                store.CreateUninitializedItem(null, sessionId, 10);
                Assert.AreEqual(1, GetItemCount(table));

                bool locked;
                TimeSpan lockAge;
                object lockId;
                SessionStateActions actionFlags;
                store.GetItem(null, sessionId, out locked, out lockAge, out lockId, out actionFlags);

                Thread.Sleep(newTimeout);

                DynamoDBSessionStateStore.DeleteExpiredSessions(client, tableName);
                Assert.AreEqual(0, GetItemCount(table));
            }
        }

        private static int GetItemCount(Table table)
        {
            Assert.IsNotNull(table);

            var allItems = table.Scan(new ScanFilter()).GetRemaining().ToList();
            return allItems.Count;
        }
        private static void WaitUntilTableReady(AmazonDynamoDBClient client, TableStatus targetStatus)
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
