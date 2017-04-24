﻿using System;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;

using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Hangfire.AzureDocumentDB.Queue;
using Hangfire.AzureDocumentDB.Entities;

namespace Hangfire.AzureDocumentDB
{
    internal sealed class AzureDocumentDbConnection : JobStorageConnection
    {
        public AzureDocumentDbStorage Storage { get; }
        public PersistentJobQueueProviderCollection QueueProviders { get; }

        private readonly FeedOptions QueryOptions = new FeedOptions { MaxItemCount = -1 };
        private readonly Uri JobDocumentCollectionUri;
        private readonly Uri StateDocumentCollectionUri;
        private readonly Uri SetDocumentCollectionUri;
        private readonly Uri CounterDocumentCollectionUri;
        private readonly Uri ServerDocumentCollectionUri;
        private readonly Uri HashDocumentCollectionUri;
        private readonly Uri ListDocumentCollectionUri;

        public AzureDocumentDbConnection(AzureDocumentDbStorage storage)
        {
            Storage = storage;
            QueueProviders = storage.QueueProviders;

            JobDocumentCollectionUri = UriFactory.CreateDocumentCollectionUri(Storage.Options.DatabaseName, "jobs");
            StateDocumentCollectionUri = UriFactory.CreateDocumentCollectionUri(Storage.Options.DatabaseName, "states");
            SetDocumentCollectionUri = UriFactory.CreateDocumentCollectionUri(Storage.Options.DatabaseName, "sets");
            CounterDocumentCollectionUri = UriFactory.CreateDocumentCollectionUri(Storage.Options.DatabaseName, "counters");
            ServerDocumentCollectionUri = UriFactory.CreateDocumentCollectionUri(Storage.Options.DatabaseName, "servers");
            HashDocumentCollectionUri = UriFactory.CreateDocumentCollectionUri(Storage.Options.DatabaseName, "hashes");
            ListDocumentCollectionUri = UriFactory.CreateDocumentCollectionUri(Storage.Options.DatabaseName, "lists");
        }

        public override IDisposable AcquireDistributedLock(string resource, TimeSpan timeout) => new AzureDocumentDbDistributedLock(resource, timeout, Storage);
        public override IWriteOnlyTransaction CreateWriteTransaction() => new AzureDocumentDbWriteOnlyTransaction(this);

        #region Job

        public override string CreateExpiredJob(Common.Job job, IDictionary<string, string> parameters, DateTime createdAt, TimeSpan expireIn)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            InvocationData invocationData = InvocationData.Serialize(job);
            Entities.Job entityJob = new Entities.Job
            {
                InvocationData = invocationData,
                Arguments = invocationData.Arguments,
                CreatedOn = createdAt,
                ExpireOn = createdAt.Add(expireIn),

                Parameters = parameters.Select(p => new Parameter
                {
                    Name = p.Key,
                    Value = p.Value
                }).ToArray()
            };

            ResourceResponse<Document> response = Storage.Client.CreateDocumentAsync(JobDocumentCollectionUri, entityJob).GetAwaiter().GetResult();
            if (response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK)
            {
                return entityJob.Id;
            }

            return string.Empty;
        }

        public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
        {
            if (queues == null || queues.Length == 0) throw new ArgumentNullException(nameof(queues));

            IPersistentJobQueueProvider[] providers = queues.Select(q => QueueProviders.GetProvider(q))
                .Distinct()
                .ToArray();

            if (providers.Length != 1)
            {
                throw new InvalidOperationException($"Multiple provider instances registered for queues: {string.Join(", ", queues)}. You should choose only one type of persistent queues per server instance.");
            }

            IPersistentJobQueue persistentQueue = providers.Single().GetJobQueue();
            IFetchedJob queue = persistentQueue.Dequeue(queues, cancellationToken);
            return queue;
        }

        public override JobData GetJobData(string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            Entities.Job data = Storage.Client.CreateDocumentQuery<Entities.Job>(JobDocumentCollectionUri, QueryOptions)
                .Where(j => j.Id == jobId)
                .AsEnumerable()
                .FirstOrDefault();

            if (data != null)
            {
                InvocationData invocationData = data.InvocationData;
                invocationData.Arguments = data.Arguments;

                Common.Job job = null;
                JobLoadException loadException = null;

                try
                {
                    job = invocationData.Deserialize();
                }
                catch (JobLoadException ex)
                {
                    loadException = ex;
                }

                return new JobData
                {
                    Job = job,
                    State = data.StateName,
                    CreatedAt = data.CreatedOn,
                    LoadException = loadException
                };
            }

            return null;
        }

        public override StateData GetStateData(string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            string stateId = Storage.Client.CreateDocumentQuery<Entities.Job>(JobDocumentCollectionUri, QueryOptions)
                .Where(j => j.Id == jobId)
                .Select(j => j.StateId)
                .AsEnumerable()
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(stateId))
            {
                State data = Storage.Client.CreateDocumentQuery<State>(StateDocumentCollectionUri, QueryOptions)
                    .Where(j => j.Id == stateId)
                    .AsEnumerable()
                    .FirstOrDefault();

                if (data != null)
                {
                    return new StateData
                    {
                        Name = data.Name,
                        Reason = data.Reason,
                        Data = data.Data.Trasnform()
                    };
                }
            }

            return null;
        }

        #endregion

        #region Parameter

        public override string GetJobParameter(string id, string name)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (name == null) throw new ArgumentNullException(nameof(name));

            List<Parameter> parameters = Storage.Client.CreateDocumentQuery<Entities.Job>(JobDocumentCollectionUri, QueryOptions)
                .Where(j => j.Id == id)
                .AsEnumerable()
                .SelectMany(j => j.Parameters)
                .ToList();

            return parameters.Where(p => p.Name == name).Select(p => p.Value).FirstOrDefault();
        }

        public override void SetJobParameter(string id, string name, string value)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (name == null) throw new ArgumentNullException(nameof(name));

            Entities.Job job = Storage.Client.CreateDocumentQuery<Entities.Job>(JobDocumentCollectionUri, QueryOptions)
                .Where(j => j.Id == id)
                .AsEnumerable()
                .FirstOrDefault();

            if (job != null)
            {
                List<Parameter> parameters = job.Parameters.ToList();

                Parameter parameter = parameters.Find(p => p.Name == name);
                if (parameter != null) parameter.Value = value;
                else
                {
                    parameter = new Parameter
                    {
                        Name = name,
                        Value = value
                    };
                    parameters.Add(parameter);
                }

                job.Parameters = parameters.ToArray();
                Storage.Client.ReplaceDocumentAsync(job.SelfLink, job).GetAwaiter().GetResult();
            }
        }

        #endregion

        #region Set

        public override TimeSpan GetSetTtl(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            DateTime? expireOn = Storage.Client.CreateDocumentQuery<Set>(SetDocumentCollectionUri, QueryOptions)
                .Where(s => s.Key == key)
                .Min(s => s.ExpireOn);

            return expireOn.HasValue ? expireOn.Value - DateTime.UtcNow : TimeSpan.FromSeconds(-1);
        }

        public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return Storage.Client.CreateDocumentQuery<Set>(SetDocumentCollectionUri, QueryOptions)
                .Where(s => s.Key == key)
                .AsEnumerable()
                .Skip(startingFrom).Take(endingAt)
                .Select(c => c.Value)
                .ToList();
        }

        public override long GetCounter(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return Storage.Client.CreateDocumentQuery<Counter>(CounterDocumentCollectionUri, QueryOptions)
                .Where(c => c.Key == key)
                .Sum(c => c.Value);
        }

        public override long GetSetCount(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return Storage.Client.CreateDocumentQuery<Set>(SetDocumentCollectionUri, QueryOptions)
                .Where(s => s.Key == key)
                .LongCount();
        }

        public override HashSet<string> GetAllItemsFromSet(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            List<string> sets = Storage.Client.CreateDocumentQuery<Set>(SetDocumentCollectionUri, QueryOptions)
                .Where(s => s.Key == key)
                .Select(s => s.Value)
                .AsEnumerable()
                .ToList();

            return new HashSet<string>(sets);
        }

        public override string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (toScore < fromScore) throw new ArgumentException("The `toScore` value must be higher or equal to the `fromScore` value.");

            return Storage.Client.CreateDocumentQuery<Set>(SetDocumentCollectionUri, QueryOptions)
                .Where(s => s.Key == key)
                .OrderBy(s => s.Score)
                .Where(s => s.Score >= fromScore && s.Score <= toScore)
                .Select(s => s.Value)
                .AsEnumerable()
                .FirstOrDefault();
        }

        #endregion

        #region Server

        public override void AnnounceServer(string serverId, ServerContext context)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));
            if (context == null) throw new ArgumentNullException(nameof(context));

            Entities.Server server = Storage.Client.CreateDocumentQuery<Entities.Server>(ServerDocumentCollectionUri, QueryOptions)
                .Where(s => s.ServerId == serverId)
                .AsEnumerable()
                .FirstOrDefault();

            if (server != null)
            {
                server.LastHeartbeat = DateTime.UtcNow;
                server.Workers = context.WorkerCount;
                server.Queues = context.Queues;
            }
            else
            {
                server = new Entities.Server
                {
                    ServerId = serverId,
                    Workers = context.WorkerCount,
                    Queues = context.Queues,
                    CreatedOn = DateTime.UtcNow,
                    LastHeartbeat = DateTime.UtcNow
                };
            }

            Storage.Client.UpsertDocumentAsync(ServerDocumentCollectionUri, server).GetAwaiter().GetResult();
        }

        public override void Heartbeat(string serverId)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));

            Entities.Server server = Storage.Client.CreateDocumentQuery<Entities.Server>(ServerDocumentCollectionUri, QueryOptions)
                .Where(s => s.ServerId == serverId)
                .AsEnumerable()
                .FirstOrDefault();

            if (server != null)
            {
                server.LastHeartbeat = DateTime.UtcNow;
                Storage.Client.ReplaceDocumentAsync(server.SelfLink, server).GetAwaiter().GetResult();
            }
        }

        public override void RemoveServer(string serverId)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));

            Entities.Server server = Storage.Client.CreateDocumentQuery<Entities.Server>(ServerDocumentCollectionUri, QueryOptions)
                .Where(s => s.ServerId == serverId)
                .AsEnumerable()
                .FirstOrDefault();

            if (server != null)
            {
                Storage.Client.DeleteDocumentAsync(server.SelfLink).GetAwaiter().GetResult();
            }
        }

        public override int RemoveTimedOutServers(TimeSpan timeOut)
        {
            if (timeOut.Duration() != timeOut)
            {
                throw new ArgumentException(@"The `timeOut` value must be positive.", nameof(timeOut));
            }

            DateTime lastHeartbeat = DateTime.UtcNow.Add(timeOut.Negate());
            string[] selfLinks = Storage.Client.CreateDocumentQuery<Entities.Server>(ServerDocumentCollectionUri, QueryOptions)
                .AsEnumerable()
                .Where(s => s.LastHeartbeat < lastHeartbeat)
                .Select(s => s.SelfLink)
                .ToArray();

            Array.ForEach(selfLinks, selfLink => Storage.Client.DeleteDocumentAsync(selfLink).GetAwaiter().GetResult());
            return selfLinks.Length;
        }

        #endregion

        #region Hash

        public override Dictionary<string, string> GetAllEntriesFromHash(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return Storage.Client.CreateDocumentQuery<Hash>(HashDocumentCollectionUri, QueryOptions)
                .Where(h => h.Key == key)
                .AsEnumerable()
                .ToDictionary(h => h.Field, h => h.Value);
        }

        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (keyValuePairs == null) throw new ArgumentNullException(nameof(keyValuePairs));

            List<Hash> sources = keyValuePairs.Select(k => new Hash
            {
                Key = key,
                Field = k.Key,
                Value = k.Value
            }).ToList();

            List<Hash> hashes = Storage.Client.CreateDocumentQuery<Hash>(HashDocumentCollectionUri, QueryOptions)
                .Where(h => h.Key == key)
                .AsEnumerable()
                .ToList();

            sources.ForEach(source =>
            {
                Hash hash = hashes.Where(h => h.Key == source.Key && h.Field == source.Field).FirstOrDefault();
                if (hash != null) source.Id = hash.Id;
            });

            sources.ForEach(hash => Storage.Client.UpsertDocumentAsync(HashDocumentCollectionUri, hash).GetAwaiter().GetResult());
        }

        public override long GetHashCount(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return Storage.Client.CreateDocumentQuery<Hash>(HashDocumentCollectionUri, QueryOptions)
                .Where(h => h.Key == key)
                .LongCount();
        }

        public override string GetValueFromHash(string key, string name)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (name == null) throw new ArgumentNullException(nameof(name));

            return Storage.Client.CreateDocumentQuery<Hash>(HashDocumentCollectionUri, QueryOptions)
                .Where(h => h.Key == key && h.Field == name)
                .Select(h => h.Value)
                .AsEnumerable()
                .FirstOrDefault();
        }

        public override TimeSpan GetHashTtl(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            DateTime? expireOn = Storage.Client.CreateDocumentQuery<Hash>(HashDocumentCollectionUri, QueryOptions)
                .Where(h => h.Key == key)
                .Min(h => h.ExpireOn);

            return expireOn.HasValue ? expireOn.Value - DateTime.UtcNow : TimeSpan.FromSeconds(-1);
        }

        #endregion

        #region List

        public override List<string> GetAllItemsFromList(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return Storage.Client.CreateDocumentQuery<List>(ListDocumentCollectionUri, QueryOptions)
                .Where(l => l.Key == key)
                .Select(l => l.Value)
                .AsEnumerable()
                .ToList();
        }

        public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return Storage.Client.CreateDocumentQuery<List>(ListDocumentCollectionUri, QueryOptions)
                .Where(l => l.Key == key)
                .AsEnumerable()
                .OrderBy(l => l.ExpireOn)
                .Skip(startingFrom).Take(endingAt)
                .Select(l => l.Value)
                .ToList();
        }

        public override TimeSpan GetListTtl(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            DateTime? expireOn = Storage.Client.CreateDocumentQuery<List>(ListDocumentCollectionUri, QueryOptions)
                .Where(l => l.Key == key)
                .Min(l => l.ExpireOn);

            return expireOn.HasValue ? expireOn.Value - DateTime.UtcNow : TimeSpan.FromSeconds(-1);
        }

        public override long GetListCount(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            return Storage.Client.CreateDocumentQuery<List>(ListDocumentCollectionUri, QueryOptions)
                .Where(l => l.Key == key)
                .LongCount();
        }

        #endregion

    }
}