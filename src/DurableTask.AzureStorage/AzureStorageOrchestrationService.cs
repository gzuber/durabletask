﻿//  ----------------------------------------------------------------------------------
//  Copyright Microsoft Corporation
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  ----------------------------------------------------------------------------------

namespace DurableTask.AzureStorage
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Runtime.ExceptionServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using DurableTask.AzureStorage.Messaging;
    using DurableTask.AzureStorage.Monitoring;
    using DurableTask.AzureStorage.Partitioning;
    using DurableTask.AzureStorage.Tracking;
    using DurableTask.Core;
    using DurableTask.Core.History;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;
    using Microsoft.WindowsAzure.Storage.Queue;
    using Newtonsoft.Json;

    /// <summary>
    /// Orchestration service provider for the Durable Task Framework which uses Azure Storage as the durable store.
    /// </summary>
    public sealed class AzureStorageOrchestrationService :
        IOrchestrationService,
        IOrchestrationServiceClient,
        IPartitionObserver<BlobLease>,
        IDisposable
    {
        static readonly HistoryEvent[] EmptyHistoryEventList = new HistoryEvent[0];
        static readonly OrchestrationInstance EmptySourceInstance = new OrchestrationInstance
        {
            InstanceId = string.Empty,
            ExecutionId = string.Empty
        };

        readonly AzureStorageOrchestrationServiceSettings settings;
        readonly AzureStorageOrchestrationServiceStats stats;
        readonly string storageAccountName;
        readonly CloudQueueClient queueClient;
        readonly CloudBlobClient blobClient;
        readonly ConcurrentDictionary<string, ControlQueue> allControlQueues;
        readonly WorkItemQueue workItemQueue;
        readonly ConcurrentDictionary<string, ActivitySession> activeActivitySessions;
        readonly MessageManager messageManager;

        readonly ITrackingStore trackingStore;

        readonly TableEntityConverter tableEntityConverter;

        readonly ResettableLazy<Task> taskHubCreator;
        readonly BlobLeaseManager leaseManager;
        readonly PartitionManager<BlobLease> partitionManager;
        readonly OrchestrationSessionManager orchestrationSessionManager;
        readonly object hubCreationLock;

        bool isStarted;
        Task statsLoop;
        CancellationTokenSource shutdownSource;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureStorageOrchestrationService"/> class.
        /// </summary>
        /// <param name="settings">The settings used to configure the orchestration service.</param>
        public AzureStorageOrchestrationService(AzureStorageOrchestrationServiceSettings settings)
            : this(settings, null)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureStorageOrchestrationService"/> class with a custom instance store.
        /// </summary>
        /// <param name="settings">The settings used to configure the orchestration service.</param>
        /// <param name="customInstanceStore">Custom UserDefined Instance store to be used with the AzureStorageOrchestrationService</param>
        public AzureStorageOrchestrationService(AzureStorageOrchestrationServiceSettings settings, IOrchestrationServiceInstanceStore customInstanceStore)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            ValidateSettings(settings);

            this.settings = settings;
            this.tableEntityConverter = new TableEntityConverter();
 
            CloudStorageAccount account = settings.StorageAccountDetails == null
                ? CloudStorageAccount.Parse(settings.StorageConnectionString)
                : settings.StorageAccountDetails.ToCloudStorageAccount();

            this.storageAccountName = account.Credentials.AccountName;
            this.stats = new AzureStorageOrchestrationServiceStats();
            this.queueClient = account.CreateCloudQueueClient();
            this.queueClient.BufferManager = SimpleBufferManager.Shared;
            this.blobClient = account.CreateCloudBlobClient();
            this.blobClient.BufferManager = SimpleBufferManager.Shared;

            string compressedMessageBlobContainerName = $"{settings.TaskHubName.ToLowerInvariant()}-largemessages";
            NameValidator.ValidateContainerName(compressedMessageBlobContainerName);
            this.messageManager = new MessageManager(this.blobClient, compressedMessageBlobContainerName);

            this.allControlQueues = new ConcurrentDictionary<string, ControlQueue>();
            for (int i = 0; i < this.settings.PartitionCount; i++)
            {
                CloudQueue controlStorageQueue = GetControlQueue(this.queueClient, this.settings.TaskHubName, i);
                ControlQueue controlQueue = new ControlQueue(controlStorageQueue, this.settings, this.stats, this.messageManager);
                this.allControlQueues.TryAdd(controlQueue.Name, controlQueue);
            }

            CloudQueue workItemStorageQueue = GetWorkItemQueue(account, settings.TaskHubName);
            this.workItemQueue = new WorkItemQueue(workItemStorageQueue, this.settings, this.stats, this.messageManager);

            if (customInstanceStore == null)
            {
                if (settings.HasTrackingStoreStorageAccount)
                {
                    this.trackingStore = new AzureTableTrackingStore(settings, this.messageManager, this.stats, settings.TrackingStoreStorageAccountDetails.ToCloudStorageAccount());
                }
                else
                {
                    this.trackingStore = new AzureTableTrackingStore(settings, this.messageManager, this.stats, account);
                }
            }
            else
            {
                this.trackingStore = new InstanceStoreBackedTrackingStore(customInstanceStore);
            }

            this.activeActivitySessions = new ConcurrentDictionary<string, ActivitySession>(StringComparer.OrdinalIgnoreCase);

            this.hubCreationLock = new object();
            this.taskHubCreator = new ResettableLazy<Task>(
                this.GetTaskHubCreatorTask,
                LazyThreadSafetyMode.ExecutionAndPublication);

            this.leaseManager = GetBlobLeaseManager(
                settings.TaskHubName,
                settings.WorkerId,
                account,
                settings.LeaseInterval,
                settings.LeaseRenewInterval,
                this.stats);
            this.partitionManager = new PartitionManager<BlobLease>(
                this.storageAccountName,
                this.settings.TaskHubName,
                settings.WorkerId,
                this.leaseManager,
                new PartitionManagerOptions
                {
                    AcquireInterval = settings.LeaseAcquireInterval,
                    RenewInterval = settings.LeaseRenewInterval,
                    LeaseInterval = settings.LeaseInterval,
                });

            this.orchestrationSessionManager = new OrchestrationSessionManager(
                this.storageAccountName,
                this.settings,
                this.stats,
                this.trackingStore);
        }

        internal string WorkerId => this.settings.WorkerId;

        internal IEnumerable<ControlQueue> AllControlQueues => this.allControlQueues.Values;

        internal IEnumerable<ControlQueue> OwnedControlQueues => this.orchestrationSessionManager.Queues;

        internal WorkItemQueue WorkItemQueue => this.workItemQueue;

        internal ITrackingStore TrackingStore => this.trackingStore;

        internal static CloudQueue GetControlQueue(CloudStorageAccount account, string taskHub, int partitionIndex)
        {
            if (account == null)
            {
                throw new ArgumentNullException(nameof(account));
            }

            return GetControlQueue(account.CreateCloudQueueClient(), taskHub, partitionIndex);
        }

        internal static CloudQueue GetControlQueue(CloudQueueClient queueClient, string taskHub, int partitionIndex)
        {
            return GetQueueInternal(queueClient, taskHub, $"control-{partitionIndex:00}");
        }

        internal static CloudQueue GetWorkItemQueue(CloudStorageAccount account, string taskHub)
        {
            if (account == null)
            {
                throw new ArgumentNullException(nameof(account));
            }

            return GetQueueInternal(account.CreateCloudQueueClient(), taskHub, "workitems");
        }

        static CloudQueue GetQueueInternal(CloudQueueClient queueClient, string taskHub, string suffix)
        {
            if (queueClient == null)
            {
                throw new ArgumentNullException(nameof(queueClient));
            }

            if (string.IsNullOrEmpty(taskHub))
            {
                throw new ArgumentNullException(nameof(taskHub));
            }

            string queueName = $"{taskHub.ToLowerInvariant()}-{suffix}";
            NameValidator.ValidateQueueName(queueName);

            return queueClient.GetQueueReference(queueName);
        }

        static BlobLeaseManager GetBlobLeaseManager(
            string taskHub,
            string workerName,
            CloudStorageAccount account,
            TimeSpan leaseInterval,
            TimeSpan renewalInterval,
            AzureStorageOrchestrationServiceStats stats)
        {
            return new BlobLeaseManager(
                taskHubName: taskHub,
                workerName: workerName,
                leaseContainerName: taskHub.ToLowerInvariant() + "-leases",
                blobPrefix: string.Empty,
                consumerGroupName: "default",
                storageClient: account.CreateCloudBlobClient(),
                leaseInterval: leaseInterval,
                renewInterval: renewalInterval,
                skipBlobContainerCreation: false,
                stats: stats);
        }

        static void ValidateSettings(AzureStorageOrchestrationServiceSettings settings)
        {
            if (settings.ControlQueueBatchSize > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "The control queue batch size must not exceed 32.");
            }

            if (settings.PartitionCount < 1 || settings.PartitionCount > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "The number of partitions must be a positive integer and no greater than 16.");
            }

            // TODO: More validation.
        }

        #region IOrchestrationService
        /// <summary>
        /// Gets or sets the maximum number of orchestrations that can be processed concurrently on a single node.
        /// </summary>
        public int MaxConcurrentTaskOrchestrationWorkItems
        {
            get { return this.settings.MaxConcurrentTaskOrchestrationWorkItems; }
        }

        /// <summary>
        /// Gets or sets the maximum number of work items that can be processed concurrently on a single node.
        /// </summary>
        public int MaxConcurrentTaskActivityWorkItems
        {
            get { return this.settings.MaxConcurrentTaskActivityWorkItems; }
        }

        /// <summary>
        ///  Should we carry over unexecuted raised events to the next iteration of an orchestration on ContinueAsNew
        /// </summary>
        public BehaviorOnContinueAsNew EventBehaviourForContinueAsNew
        {
            get { return this.settings.EventBehaviourForContinueAsNew; }
        }

        // We always leave the dispatcher counts at one unless we can find a customer workload that requires more.
        /// <inheritdoc />
        public int TaskActivityDispatcherCount { get; } = 1;

        /// <inheritdoc />
        public int TaskOrchestrationDispatcherCount { get; } = 1;

        #region Management Operations (Create/Delete/Start/Stop)
        /// <summary>
        /// Deletes and creates the neccesary Azure Storage resources for the orchestration service.
        /// </summary>
        public async Task CreateAsync()
        {
            await this.DeleteAsync();
            await this.EnsureTaskHubAsync();
        }

        /// <summary>
        /// Creates the necessary Azure Storage resources for the orchestration service if they don't already exist.
        /// </summary>
        public Task CreateIfNotExistsAsync()
        {
            return this.EnsureTaskHubAsync();
        }

        async Task EnsureTaskHubAsync()
        {
            try
            {
                await this.taskHubCreator.Value;
            }
            catch (Exception e)
            {
                AnalyticsEventSource.Log.GeneralError(
                    this.storageAccountName,
                    this.settings.TaskHubName,
                    $"Failed to create the task hub: {e}",
                    Utils.ExtensionVersion);

                // Don't want to cache the failed task
                this.taskHubCreator.Reset();
                throw;
            }
        }

        // Internal logic used by the lazy taskHubCreator
        async Task GetTaskHubCreatorTask()
        {
            TaskHubInfo hubInfo = GetTaskHubInfo(this.settings.TaskHubName, this.settings.PartitionCount);
            await this.leaseManager.CreateLeaseStoreIfNotExistsAsync(hubInfo);
            this.stats.StorageRequests.Increment();

            var tasks = new List<Task>();

            tasks.Add(this.trackingStore.CreateAsync());

            tasks.Add(this.workItemQueue.CreateIfNotExistsAsync());

            foreach (ControlQueue controlQueue in this.allControlQueues.Values)
            {
                tasks.Add(controlQueue.CreateIfNotExistsAsync());
                tasks.Add(this.leaseManager.CreateLeaseIfNotExistAsync(controlQueue.Name));
            }

            await Task.WhenAll(tasks.ToArray());
            this.stats.StorageRequests.Increment(tasks.Count);
        }

        /// <summary>
        /// Deletes the Azure Storage resources used by the orchestration service.
        /// </summary>
        public Task DeleteAsync()
        {
            return this.DeleteAsync(deleteInstanceStore: true);
        }

        /// <inheritdoc />
        public async Task CreateAsync(bool recreateInstanceStore)
        {
            if (recreateInstanceStore)
            {
                await DeleteTrackingStore();

                this.taskHubCreator.Reset();
            }

            await this.taskHubCreator.Value;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(bool deleteInstanceStore)
        {
            var tasks = new List<Task>();

            foreach (string partitionId in this.allControlQueues.Keys)
            {
                if (this.allControlQueues.TryGetValue(partitionId, out ControlQueue controlQueue))
                {
                    tasks.Add(controlQueue.DeleteIfExistsAsync());
                }
            }

            tasks.Add(this.workItemQueue.DeleteIfExistsAsync());

            if (deleteInstanceStore)
            {
                tasks.Add(DeleteTrackingStore());
            }

            tasks.Add(this.leaseManager.DeleteAllAsync().ContinueWith(t =>
            {
                if (t.Exception?.InnerExceptions?.Count > 0)
                {
                    foreach (Exception e in t.Exception.InnerExceptions)
                    {
                        StorageException storageException = e as StorageException;
                        if (storageException == null || storageException.RequestInformation.HttpStatusCode != 404)
                        {
                            ExceptionDispatchInfo.Capture(e).Throw();
                        }
                    }
                }
            }));

            tasks.Add(this.messageManager.DeleteContainerAsync());

            await Task.WhenAll(tasks.ToArray());
            this.stats.StorageRequests.Increment(tasks.Count);
            this.taskHubCreator.Reset();
        }

        private Task DeleteTrackingStore()
        {
            return this.trackingStore.DeleteAsync();
        }

        /// <inheritdoc />
        public async Task StartAsync()
        {
            if (this.isStarted)
            {
                throw new InvalidOperationException("The orchestration service has already started.");
            }

            await this.trackingStore.StartAsync();

            // Disable nagling to improve storage access latency:
            // https://blogs.msdn.microsoft.com/windowsazurestorage/2010/06/25/nagles-algorithm-is-not-friendly-towards-small-requests/
            // Ad-hoc testing has shown very nice improvements (20%-50% drop in queue message age for simple scenarios).
            ServicePointManager.FindServicePoint(this.workItemQueue.Uri).UseNagleAlgorithm = false;

            this.shutdownSource?.Dispose();
            this.shutdownSource = new CancellationTokenSource();
            this.statsLoop = Task.Run(() => this.ReportStatsLoop(this.shutdownSource.Token));

            await this.partitionManager.InitializeAsync();
            await this.partitionManager.SubscribeAsync(this);
            await this.partitionManager.StartAsync();

            this.isStarted = true;
        }

        /// <inheritdoc />
        public Task StopAsync()
        {
            return this.StopAsync(isForced: false);
        }

        /// <inheritdoc />
        public async Task StopAsync(bool isForced)
        {
            this.shutdownSource.Cancel();
            await this.statsLoop;
            await this.partitionManager.StopAsync();
            this.isStarted = false;
        }

        async Task ReportStatsLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    this.ReportStats();
                }
                catch (TaskCanceledException)
                {
                    // shutting down
                    break;
                }
                catch (Exception e)
                {
                    AnalyticsEventSource.Log.GeneralError(
                        this.storageAccountName,
                        this.settings.TaskHubName,
                        $"Unexpected error in {nameof(ReportStatsLoop)}: {e}",
                        Utils.ExtensionVersion);
                }
            }

            // Final reporting of stats
            this.ReportStats();
        }

        void ReportStats()
        {
            // The following stats are reported on a per-interval basis.
            long storageRequests = this.stats.StorageRequests.Reset();
            long messagesSent = this.stats.MessagesSent.Reset();
            long messagesRead = this.stats.MessagesRead.Reset();
            long messagesUpdated = this.stats.MessagesUpdated.Reset();
            long tableEntitiesWritten = this.stats.TableEntitiesWritten.Reset();
            long tableEntitiesRead = this.stats.TableEntitiesRead.Reset();

            // The remaining stats are running numbers
            this.orchestrationSessionManager.GetStats(
                out int pendingOrchestratorInstances,
                out int pendingOrchestrationMessages,
                out int activeOrchestrationSessions);

            AnalyticsEventSource.Log.OrchestrationServiceStats(
                this.storageAccountName,
                this.settings.TaskHubName,
                storageRequests,
                messagesSent,
                messagesRead,
                messagesUpdated,
                tableEntitiesWritten,
                tableEntitiesRead,
                pendingOrchestratorInstances,
                pendingOrchestrationMessages,
                activeOrchestrationSessions,
                this.stats.ActiveActivityExecutions.Value,
                Utils.ExtensionVersion);
        }

        async Task IPartitionObserver<BlobLease>.OnPartitionAcquiredAsync(BlobLease lease)
        {
            CloudQueue storageQueue = this.queueClient.GetQueueReference(lease.PartitionId);
            await storageQueue.CreateIfNotExistsAsync();
            this.stats.StorageRequests.Increment();

            var controlQueue = new ControlQueue(storageQueue, this.settings, this.stats, this.messageManager);
            this.orchestrationSessionManager.AddQueue(lease.PartitionId, controlQueue, this.shutdownSource.Token);

            this.allControlQueues[lease.PartitionId] = controlQueue;
        }

        Task IPartitionObserver<BlobLease>.OnPartitionReleasedAsync(BlobLease lease, CloseReason reason)
        {
            this.orchestrationSessionManager.RemoveQueue(lease.PartitionId);
            return Utils.CompletedTask;
        }

        // Used for testing
        internal Task<IEnumerable<BlobLease>> ListBlobLeasesAsync()
        {
            return this.leaseManager.ListLeasesAsync();
        }

        internal static async Task<CloudQueue[]> GetControlQueuesAsync(
            CloudStorageAccount account,
            string taskHub,
            int defaultPartitionCount)
        {
            if (account == null)
            {
                throw new ArgumentNullException(nameof(account));
            }

            if (taskHub == null)
            {
                throw new ArgumentNullException(nameof(taskHub));
            }

            BlobLeaseManager inactiveLeaseManager = GetBlobLeaseManager(taskHub, "n/a", account, TimeSpan.Zero, TimeSpan.Zero, null);
            TaskHubInfo hubInfo = await inactiveLeaseManager.GetOrCreateTaskHubInfoAsync(
                GetTaskHubInfo(taskHub, defaultPartitionCount));

            CloudQueueClient queueClient = account.CreateCloudQueueClient();

            var controlQueues = new CloudQueue[hubInfo.PartitionCount];
            for (int i = 0; i < hubInfo.PartitionCount; i++)
            {
                controlQueues[i] = GetControlQueue(queueClient, taskHub, i);
            }

            return controlQueues;
        }

        static TaskHubInfo GetTaskHubInfo(string taskHub, int partitionCount)
        {
            return new TaskHubInfo(taskHub, DateTime.UtcNow, partitionCount);
        }

        #endregion

        #region Orchestration Work Item Methods
        /// <inheritdoc />
        public async Task<TaskOrchestrationWorkItem> LockNextTaskOrchestrationWorkItemAsync(
            TimeSpan receiveTimeout,
            CancellationToken cancellationToken)
        {
            Guid traceActivityId = StartNewLogicalTraceScope();

            await this.EnsureTaskHubAsync();

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.shutdownSource.Token))
            {
                OrchestrationSession session = null;
                TaskOrchestrationWorkItem orchestrationWorkItem = null;

                try
                {
                    // This call will block until the next session is ready
                    session = await this.orchestrationSessionManager.GetNextSessionAsync(linkedCts.Token);
                    if (session == null)
                    {
                        return null;
                    }

                    session.StartNewLogicalTraceScope();

                    List<MessageData> outOfOrderMessages = null;
                    foreach (MessageData message in session.CurrentMessageBatch)
                    {
                        if (session.IsOutOfOrderMessage(message))
                        {
                            if (outOfOrderMessages == null)
                            {
                                outOfOrderMessages = new List<MessageData>();
                            }

                            // This can happen if a lease change occurs and a new node receives a message for an 
                            // orchestration that has not yet checkpointed its history. We abandon such messages
                            // so that they can be reprocessed after the history checkpoint has completed.
                            AnalyticsEventSource.Log.ReceivedOutOfOrderMessage(
                                this.storageAccountName,
                                this.settings.TaskHubName,
                                session.Instance.InstanceId,
                                session.Instance.ExecutionId,
                                session.ControlQueue.Name,
                                message.TaskMessage.Event.EventType.ToString(),
                                Utils.GetTaskEventId(message.TaskMessage.Event),
                                message.OriginalQueueMessage.Id,
                                message.Episode.GetValueOrDefault(-1),
                                session.LastCheckpointTime,
                                Utils.ExtensionVersion);
                            outOfOrderMessages.Add(message);
                        }
                        else
                        {
                            session.TraceProcessingMessage(message, isExtendedSession: false);
                        }
                    }

                    if (outOfOrderMessages?.Count > 0)
                    {
                        // This will also remove the messages from the current batch.
                        await this.AbandonMessagesAsync(session, outOfOrderMessages);
                    }

                    if (session.CurrentMessageBatch.Count == 0)
                    {
                        // All messages were removed. Release the work item.
                        await this.AbandonAndReleaseSessionAsync(session);
                        return null;
                    }

                    orchestrationWorkItem = new TaskOrchestrationWorkItem
                    {
                        InstanceId = session.Instance.InstanceId,
                        LockedUntilUtc = session.CurrentMessageBatch.Min(msg => msg.OriginalQueueMessage.NextVisibleTime.Value.UtcDateTime),
                        NewMessages = session.CurrentMessageBatch.Select(m => m.TaskMessage).ToList(),
                        OrchestrationRuntimeState = session.RuntimeState,
                        Session = this.settings.ExtendedSessionsEnabled ? session : null,
                    };

                    if (!this.IsExecutableInstance(session.RuntimeState, orchestrationWorkItem.NewMessages, out string warningMessage))
                    {
                        // If all messages belong to the same execution ID, then all of them need to be discarded.
                        // However, it's also possible to have messages for *any* execution ID batched together with messages
                        // to a *specific* (non-executable) execution ID. Those messages should *not* be discarded since
                        // they might be consumable by another orchestration with the same instance id but different execution ID.
                        var messagesToDiscard = new List<MessageData>();
                        var messagesToAbandon = new List<MessageData>();
                        foreach (MessageData msg in session.CurrentMessageBatch)
                        {
                            if (msg.TaskMessage.OrchestrationInstance.ExecutionId == session.Instance.ExecutionId)
                            {
                                messagesToDiscard.Add(msg);
                            }
                            else
                            {
                                messagesToAbandon.Add(msg);
                            }
                        }

                        // If no messages have a matching execution ID, then delete all of them. This means all the
                        // messages are external (external events, termination, etc.) and were sent to an instance that
                        // doesn't exist or is no longer in a running state.
                        if (messagesToDiscard.Count == 0)
                        {
                            messagesToDiscard.AddRange(messagesToAbandon);
                            messagesToAbandon.Clear();
                        }

                        // Add all abandoned messages to the deferred list. These messages will not be deleted right now.
                        // If they can be matched with another orchestration, then great. Otherwise they will be deleted
                        // the next time they are picked up.
                        messagesToAbandon.ForEach(session.DeferMessage);

                        var eventListBuilder = new StringBuilder(orchestrationWorkItem.NewMessages.Count * 40);
                        foreach (MessageData msg in messagesToDiscard)
                        {
                            eventListBuilder.Append(msg.TaskMessage.Event.EventType.ToString()).Append(',');
                        }

                        AnalyticsEventSource.Log.DiscardingWorkItem(
                            this.storageAccountName,
                            this.settings.TaskHubName,
                            session.Instance.InstanceId,
                            session.Instance.ExecutionId,
                            orchestrationWorkItem.NewMessages.Count,
                            session.RuntimeState.Events.Count,
                            eventListBuilder.ToString(0, eventListBuilder.Length - 1) /* remove trailing comma */,
                            warningMessage,
                            Utils.ExtensionVersion);

                        // The instance has already completed or never existed. Delete this message batch.
                        await this.DeleteMessageBatchAsync(session, messagesToDiscard);
                        await this.ReleaseTaskOrchestrationWorkItemAsync(orchestrationWorkItem);
                        return null;
                    }

                    return orchestrationWorkItem;
                }
                catch (OperationCanceledException)
                {
                    if (session != null)
                    {
                        // host is shutting down - release any queued messages
                        await this.AbandonAndReleaseSessionAsync(session);
                    }

                    return null;
                }
                catch (Exception e)
                {
                    AnalyticsEventSource.Log.OrchestrationProcessingFailure(
                        this.storageAccountName,
                        this.settings.TaskHubName,
                        session?.Instance.InstanceId ?? string.Empty,
                        session?.Instance.ExecutionId ?? string.Empty,
                        e.ToString(),
                        Utils.ExtensionVersion);

                    if (orchestrationWorkItem != null)
                    {
                        // The work-item needs to be released so that it can be retried later.
                        await this.ReleaseTaskOrchestrationWorkItemAsync(orchestrationWorkItem);
                    }

                    throw;
                }
            }
        }

        internal static Guid StartNewLogicalTraceScope()
        {
            // This call sets the activity trace ID both on the current thread context
            // and on the logical call context. AnalyticsEventSource will use this 
            // activity ID for all trace operations.
            Guid traceActivityId = Guid.NewGuid();
            AnalyticsEventSource.SetLogicalTraceActivityId(traceActivityId);
            return traceActivityId;
        }

        internal static void TraceMessageReceived(MessageData data, string storageAccountName, string taskHubName)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            TaskMessage taskMessage = data.TaskMessage;
            CloudQueueMessage queueMessage = data.OriginalQueueMessage;

            AnalyticsEventSource.Log.ReceivedMessage(
                data.ActivityId,
                storageAccountName,
                taskHubName,
                taskMessage.Event.EventType.ToString(),
                Utils.GetTaskEventId(taskMessage.Event),
                taskMessage.OrchestrationInstance.InstanceId,
                taskMessage.OrchestrationInstance.ExecutionId,
                queueMessage.Id,
                Math.Max(0, (int)DateTimeOffset.UtcNow.Subtract(queueMessage.InsertionTime.Value).TotalMilliseconds),
                queueMessage.DequeueCount,
                queueMessage.NextVisibleTime.GetValueOrDefault().DateTime.ToString("o"),
                data.TotalMessageSizeBytes,
                data.QueueName /* PartitionId */,
                data.SequenceNumber,
                data.Episode.GetValueOrDefault(-1),
                Utils.ExtensionVersion);
        }

        bool IsExecutableInstance(OrchestrationRuntimeState runtimeState, IList<TaskMessage> newMessages, out string message)
        {
            if (runtimeState.ExecutionStartedEvent == null && !newMessages.Any(msg => msg.Event is ExecutionStartedEvent))
            {
                var instanceId = newMessages[0].OrchestrationInstance.InstanceId;

                if (instanceId.StartsWith("@"))
                {
                    // automatically start this instance
                    var orchestrationInstance = new OrchestrationInstance
                    {
                        InstanceId = instanceId,
                        ExecutionId = Guid.NewGuid().ToString("N"),
                    };
                    var startedEvent = new ExecutionStartedEvent(-1, null)
                    {
                        Name = instanceId,
                        Version = "",
                        OrchestrationInstance = orchestrationInstance
                    };
                    var taskMessage = new TaskMessage()
                    {
                        OrchestrationInstance = orchestrationInstance,
                        Event = startedEvent
                    };
                    newMessages.Insert(0, taskMessage);
                }
                else
                {
                    message = runtimeState.Events.Count == 0 ? "No such instance" : "Instance is corrupted";
                    return false;
                }
            }

            if (runtimeState.ExecutionStartedEvent != null &&
                runtimeState.OrchestrationStatus != OrchestrationStatus.Running &&
                runtimeState.OrchestrationStatus != OrchestrationStatus.Pending)
            {
                message = $"Instance is {runtimeState.OrchestrationStatus}";
                return false;
            }

            message = null;
            return true;
        }

        async Task AbandonAndReleaseSessionAsync(OrchestrationSession session)
        {
            try
            {
                await this.AbandonSessionAsync(session);
            }
            finally
            {
                await this.ReleaseSessionAsync(session.Instance.InstanceId);
            }
        }

        /// <inheritdoc />
        public async Task CompleteTaskOrchestrationWorkItemAsync(
            TaskOrchestrationWorkItem workItem,
            OrchestrationRuntimeState newOrchestrationRuntimeState,
            IList<TaskMessage> outboundMessages,
            IList<TaskMessage> orchestratorMessages,
            IList<TaskMessage> timerMessages,
            TaskMessage continuedAsNewMessage,
            OrchestrationState orchestrationState)
        {
            OrchestrationSession session;
            if (!this.orchestrationSessionManager.TryGetExistingSession(workItem.InstanceId, out session))
            {
                AnalyticsEventSource.Log.AssertFailure(
                    this.storageAccountName,
                    this.settings.TaskHubName,
                    $"{nameof(CompleteTaskOrchestrationWorkItemAsync)}: Session for instance {workItem.InstanceId} was not found!",
                    Utils.ExtensionVersion);
                return;
            }

            session.StartNewLogicalTraceScope();
            OrchestrationRuntimeState runtimeState = newOrchestrationRuntimeState ?? workItem.OrchestrationRuntimeState;

            string instanceId = workItem.InstanceId;
            string executionId = runtimeState.OrchestrationInstance.ExecutionId;

            // First, add new messages into the queue. If a failure happens after this, duplicate messages will
            // be written after the retry, but the results of those messages are expected to be de-dup'd later.
            // This provider needs to ensure that response messages are not processed until the history a few
            // lines down has been successfully committed.
            await this.CommitOutboundQueueMessages(
                session,
                outboundMessages,
                orchestratorMessages,
                timerMessages,
                continuedAsNewMessage);

            // Next, commit the orchestration history updates. This is the actual "checkpoint". Failures after this
            // will result in a duplicate replay of the orchestration with no side-effects.
            try
            {
                session.ETag = await this.trackingStore.UpdateStateAsync(runtimeState, workItem.OrchestrationRuntimeState, instanceId, executionId, session.ETag);
            }
            catch (Exception e)
            {
                // Precondition failure is expected to be handled internally and logged as a warning.
                if ((e as StorageException)?.RequestInformation?.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed)
                {
                    await this.AbandonTaskOrchestrationWorkItemAsync(workItem);
                    return;
                }
                else
                {
                    // TODO: https://github.com/Azure/azure-functions-durable-extension/issues/332
                    //       It's possible that history updates may have been partially committed at this point.
                    //       If so, what are the implications of this as far as DurableTask.Core are concerned?
                    AnalyticsEventSource.Log.OrchestrationProcessingFailure(
                        this.storageAccountName,
                        this.settings.TaskHubName,
                        instanceId,
                        executionId,
                        e.ToString(),
                        Utils.ExtensionVersion);
                }

                throw;
            }

            // Finally, delete the messages which triggered this orchestration execution. This is the final commit.
            await this.DeleteMessageBatchAsync(session, session.CurrentMessageBatch);
        }

        async Task CommitOutboundQueueMessages(
            OrchestrationSession session,
            IList<TaskMessage> outboundMessages,
            IList<TaskMessage> orchestratorMessages,
            IList<TaskMessage> timerMessages,
            TaskMessage continuedAsNewMessage)
        {
            int messageCount =
                (outboundMessages?.Count ?? 0) +
                (orchestratorMessages?.Count ?? 0) +
                (timerMessages?.Count ?? 0) +
                (continuedAsNewMessage != null ? 1 : 0);

            // Second persistence step is to commit outgoing messages to their respective queues. If there is
            // any failures here, then the messages may get written again later.
            var enqueueOperations = new List<QueueMessage>(messageCount);
            if (orchestratorMessages?.Count > 0)
            {
                foreach (TaskMessage taskMessage in orchestratorMessages)
                {
                    string targetInstanceId = taskMessage.OrchestrationInstance.InstanceId;
                    ControlQueue targetControlQueue = await this.GetControlQueueAsync(targetInstanceId);

                    enqueueOperations.Add(new QueueMessage(targetControlQueue, taskMessage));
                }
            }

            if (timerMessages?.Count > 0)
            {
                foreach (TaskMessage taskMessage in timerMessages)
                {
                    enqueueOperations.Add(new QueueMessage(session.ControlQueue, taskMessage));
                }
            }

            if (continuedAsNewMessage != null)
            {
                enqueueOperations.Add(new QueueMessage(session.ControlQueue, continuedAsNewMessage));
            }

            if (outboundMessages?.Count > 0)
            {
                foreach (TaskMessage taskMessage in outboundMessages)
                {
                    enqueueOperations.Add(new QueueMessage(this.workItemQueue, taskMessage));
                }
            }

            await enqueueOperations.ParallelForEachAsync(
                this.settings.MaxStorageOperationConcurrency,
                op => op.Queue.AddMessageAsync(op.Message, session));
        }

        async Task DeleteMessageBatchAsync(OrchestrationSession session, IList<MessageData> messagesToDelete)
        {
            await messagesToDelete.ParallelForEachAsync(
                this.settings.MaxStorageOperationConcurrency,
                message => session.ControlQueue.DeleteMessageAsync(message, session));
        }

        // REVIEW: There doesn't seem to be any code which calls this method.
        //         https://github.com/Azure/durabletask/issues/112
        /// <inheritdoc />
        public async Task RenewTaskOrchestrationWorkItemLockAsync(TaskOrchestrationWorkItem workItem)
        {
            OrchestrationSession session;
            if (!this.orchestrationSessionManager.TryGetExistingSession(workItem.InstanceId, out session))
            {
                AnalyticsEventSource.Log.AssertFailure(
                    this.storageAccountName,
                    this.settings.TaskHubName,
                    $"{nameof(RenewTaskOrchestrationWorkItemLockAsync)}: Session for instance {workItem.InstanceId} was not found!",
                    Utils.ExtensionVersion);
                return;
            }

            session.StartNewLogicalTraceScope();
            string instanceId = workItem.InstanceId;
            ControlQueue controlQueue = await this.GetControlQueueAsync(instanceId);

            // Reset the visibility of the message to ensure it doesn't get picked up by anyone else.
            await session.CurrentMessageBatch.ParallelForEachAsync(
                this.settings.MaxStorageOperationConcurrency,
                message => controlQueue.RenewMessageAsync(message, session));

            workItem.LockedUntilUtc = DateTime.UtcNow.Add(this.settings.ControlQueueVisibilityTimeout);
        }

        /// <inheritdoc />
        public Task AbandonTaskOrchestrationWorkItemAsync(TaskOrchestrationWorkItem workItem)
        {
            OrchestrationSession session;
            if (!this.orchestrationSessionManager.TryGetExistingSession(workItem.InstanceId, out session))
            {
                AnalyticsEventSource.Log.AssertFailure(
                    this.storageAccountName,
                    this.settings.TaskHubName,
                    $"{nameof(AbandonTaskOrchestrationWorkItemAsync)}: Session for instance {workItem.InstanceId} was not found!",
                    Utils.ExtensionVersion);
                return Utils.CompletedTask;
            }

            return this.AbandonSessionAsync(session);
        }

        Task AbandonSessionAsync(OrchestrationSession session)
        {
            session.StartNewLogicalTraceScope();
            return this.AbandonMessagesAsync(session, session.CurrentMessageBatch.ToList());
        }

        async Task AbandonMessagesAsync(OrchestrationSession session, IList<MessageData> messages)
        {
            await messages.ParallelForEachAsync(
                this.settings.MaxStorageOperationConcurrency,
                message => session.ControlQueue.AbandonMessageAsync(message, session));

            // Remove the messages from the current batch. The remaining messages
            // may still be able to be processed
            foreach (MessageData message in messages)
            {
                session.CurrentMessageBatch.Remove(message);
            }
        }

        // Called after an orchestration completes an execution episode and after all messages have been enqueued.
        // Also called after an orchestration work item is abandoned.
        /// <inheritdoc />
        public Task ReleaseTaskOrchestrationWorkItemAsync(TaskOrchestrationWorkItem workItem)
        {
            return this.ReleaseSessionAsync(workItem.InstanceId);
        }

        async Task ReleaseSessionAsync(string instanceId)
        {
            if (this.orchestrationSessionManager.TryReleaseSession(
                instanceId,
                this.shutdownSource.Token,
                out OrchestrationSession session))
            {
                // Some messages may need to be discarded
                await session.DiscardedMessages.ParallelForEachAsync(
                    this.settings.MaxStorageOperationConcurrency,
                    message => session.ControlQueue.DeleteMessageAsync(message, session));
            }
        }
        #endregion

        #region Task Activity Methods
        /// <inheritdoc />
        public async Task<TaskActivityWorkItem> LockNextTaskActivityWorkItem(
            TimeSpan receiveTimeout,
            CancellationToken cancellationToken)
        {
            await this.EnsureTaskHubAsync();

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.shutdownSource.Token))
            {
                MessageData message = await this.workItemQueue.GetMessageAsync(linkedCts.Token);

                if (message == null)
                {
                    // shutting down
                    return null;
                }

                Guid traceActivityId = Guid.NewGuid();
                var session = new ActivitySession(this.storageAccountName, this.settings.TaskHubName, message, traceActivityId);
                session.StartNewLogicalTraceScope();
                TraceMessageReceived(session.MessageData, this.storageAccountName, this.settings.TaskHubName);
                session.TraceProcessingMessage(message, isExtendedSession: false);

                if (!this.activeActivitySessions.TryAdd(message.Id, session))
                {
                    // This means we're already processing this message. This is never expected since the message
                    // should be kept invisible via background calls to RenewTaskActivityWorkItemLockAsync.
                    AnalyticsEventSource.Log.AssertFailure(
                        this.storageAccountName,
                        this.settings.TaskHubName,
                        $"Work item queue message with ID = {message.Id} is being processed multiple times concurrently.",
                        Utils.ExtensionVersion);
                    return null;
                }

                this.stats.ActiveActivityExecutions.Increment();

                return new TaskActivityWorkItem
                {
                    Id = message.Id,
                    TaskMessage = session.MessageData.TaskMessage,
                    LockedUntilUtc = message.OriginalQueueMessage.NextVisibleTime.Value.UtcDateTime,
                };
            }
        }

        /// <inheritdoc />
        public async Task CompleteTaskActivityWorkItemAsync(TaskActivityWorkItem workItem, TaskMessage responseTaskMessage)
        {
            ActivitySession session;
            if (!this.activeActivitySessions.TryGetValue(workItem.Id, out session))
            {
                // The context does not exist - possibly because it was already removed.
                AnalyticsEventSource.Log.AssertFailure(
                    this.storageAccountName,
                    this.settings.TaskHubName,
                    $"Could not find context for work item with ID = {workItem.Id}.",
                    Utils.ExtensionVersion);
                return;
            }

            session.StartNewLogicalTraceScope();
            string instanceId = workItem.TaskMessage.OrchestrationInstance.InstanceId;
            ControlQueue controlQueue = await this.GetControlQueueAsync(instanceId);

            // First, send a response message back. If this fails, we'll try again later since we haven't deleted the
            // work item message yet (that happens next).
            await controlQueue.AddMessageAsync(responseTaskMessage, session);

            // Next, delete the work item queue message. This must come after enqueuing the response message.
            await this.workItemQueue.DeleteMessageAsync(session.MessageData, session);

            if (this.activeActivitySessions.TryRemove(workItem.Id, out _))
            {
                this.stats.ActiveActivityExecutions.Decrement();
            }
        }

        /// <inheritdoc />
        public async Task<TaskActivityWorkItem> RenewTaskActivityWorkItemLockAsync(TaskActivityWorkItem workItem)
        {
            ActivitySession session;
            if (!this.activeActivitySessions.TryGetValue(workItem.Id, out session))
            {
                // The context does not exist - possibly because it was already removed.
                // Expire the work item to prevent subsequent renewal attempts.
                return ExpireWorkItem(workItem);
            }

            session.StartNewLogicalTraceScope();

            // Reset the visibility of the message to ensure it doesn't get picked up by anyone else.
            await this.workItemQueue.RenewMessageAsync(session.MessageData, session);

            workItem.LockedUntilUtc = DateTime.UtcNow.Add(this.settings.WorkItemQueueVisibilityTimeout);
            return workItem;
        }

        static TaskActivityWorkItem ExpireWorkItem(TaskActivityWorkItem workItem)
        {
            workItem.LockedUntilUtc = DateTime.UtcNow;
            return workItem;
        }

        /// <inheritdoc />
        public async Task AbandonTaskActivityWorkItemAsync(TaskActivityWorkItem workItem)
        {
            ActivitySession session;
            if (!this.activeActivitySessions.TryGetValue(workItem.Id, out session))
            {
                // The context does not exist - possibly because it was already removed.
                AnalyticsEventSource.Log.AssertFailure(
                    this.storageAccountName,
                    this.settings.TaskHubName,
                    $"Could not find context for work item with ID = {workItem.Id}.",
                    Utils.ExtensionVersion);
                return;
            }

            session.StartNewLogicalTraceScope();

            await this.workItemQueue.AbandonMessageAsync(session.MessageData, session);

            if (this.activeActivitySessions.TryRemove(workItem.Id, out _))
            {
                this.stats.ActiveActivityExecutions.Decrement();
            }
        }
        #endregion

        /// <inheritdoc />
        public bool IsMaxMessageCountExceeded(int currentMessageCount, OrchestrationRuntimeState runtimeState)
        {
            if (this.settings.MaxCheckpointBatchSize <= 0)
            {
                return false;
            }

            // We will process at most N events at a time. Any remaining events will be
            // scheduled in a subsequent episode. This is useful to ensure we don't exceed the
            // orchestrator queue timeout while trying to checkpoint massive numbers of actions.
            // It also reduces the amount of re-work that needs to be done if there is a failure
            // in the middle of a checkpoint. Note that having a number too small could
            // drastically increase the end-to-end processing time of an orchestration.
            return runtimeState.NewEvents.Count > this.settings.MaxCheckpointBatchSize;
        }

        /// <inheritdoc />
        public int GetDelayInSecondsAfterOnFetchException(Exception exception)
        {
            // TODO: Need to reason about exception delays
            return 10;
        }

        /// <inheritdoc />
        public int GetDelayInSecondsAfterOnProcessException(Exception exception)
        {
            // TODO: Need to reason about exception delays
            return 10;
        }
        #endregion

        #region IOrchestrationServiceClient
        /// <summary>
        /// Creates and starts a new orchestration.
        /// </summary>
        /// <param name="creationMessage">The message which creates and starts the orchestration.</param>
        public Task CreateTaskOrchestrationAsync(TaskMessage creationMessage)
        {
            return this.CreateTaskOrchestrationAsync(creationMessage, null);
        }

        /// <summary>
        /// Creates a new orchestration
        /// </summary>
        /// <param name="creationMessage">Orchestration creation message</param>
        /// <param name="dedupeStatuses">States of previous orchestration executions to be considered while de-duping new orchestrations on the client</param>
        public async Task CreateTaskOrchestrationAsync(TaskMessage creationMessage, OrchestrationStatus[] dedupeStatuses)
        {
            ExecutionStartedEvent executionStartedEvent = creationMessage.Event as ExecutionStartedEvent;
            if (executionStartedEvent == null)
            {
                throw new ArgumentException($"Only {nameof(EventType.ExecutionStarted)} messages are supported.", nameof(creationMessage));
            }

            // Client operations will auto-create the task hub if it doesn't already exist.
            await this.EnsureTaskHubAsync();

            OrchestrationState existingInstance = await this.trackingStore.GetStateAsync(
                creationMessage.OrchestrationInstance.InstanceId,
                executionId: null,
                fetchInput: false);

            if (existingInstance != null && dedupeStatuses != null && dedupeStatuses.Contains(existingInstance.OrchestrationStatus))
            {
                // An instance in this state already exists.
                if (settings.ThrowExceptionOnInvalidDedupeStatus)
                {
                    throw new InvalidOperationException($"An Orchestration instance with the status {existingInstance.OrchestrationStatus} already exists.");
                }

                return;
            }

            ControlQueue controlQueue = await this.GetControlQueueAsync(creationMessage.OrchestrationInstance.InstanceId);
            MessageData internalMessage = await this.SendTaskOrchestrationMessageInternalAsync(
                EmptySourceInstance,
                controlQueue,
                creationMessage);

            // The start message gets enqueued before we write to the instances table. It's possible that the orchestration
            // will start and update the instances status before this call can do so. To account for that race condition
            // we ignore updating the instance status if none already exists. The case where one exists already is
            // when an existing instance is being overwritten.
            bool ignoreExistingInstances = (existingInstance == null);

            // CompressedBlobName either has a blob path for large messages or is null.
            string inputStatusOverride = internalMessage.CompressedBlobName;

            await this.trackingStore.SetNewExecutionAsync(
                executionStartedEvent,
                ignoreExistingInstances,
                inputStatusOverride);
        }

        /// <summary>
        /// Sends a list of messages to an orchestration.
        /// </summary>
        /// <remarks>
        /// Azure Storage does not support batch sending to queues, so there are no transactional guarantees in this method.
        /// </remarks>
        /// <param name="messages">The list of messages to send.</param>
        public Task SendTaskOrchestrationMessageBatchAsync(params TaskMessage[] messages)
        {
            return Task.WhenAll(messages.Select(msg => this.SendTaskOrchestrationMessageAsync(msg)));
        }

        /// <summary>
        /// Sends a message to an orchestration.
        /// </summary>
        /// <param name="message">The message to send.</param>
        public async Task SendTaskOrchestrationMessageAsync(TaskMessage message)
        {
            // Client operations will auto-create the task hub if it doesn't already exist.
            await this.EnsureTaskHubAsync();
            ControlQueue controlQueue = await this.GetControlQueueAsync(message.OrchestrationInstance.InstanceId);
            await this.SendTaskOrchestrationMessageInternalAsync(EmptySourceInstance, controlQueue, message);
        }

        Task<MessageData> SendTaskOrchestrationMessageInternalAsync(
            OrchestrationInstance sourceInstance,
            ControlQueue controlQueue,
            TaskMessage message)
        {
            return controlQueue.AddMessageAsync(message, sourceInstance);
        }

        /// <summary>
        /// Get the most current execution (generation) of the specified instance.
        /// </summary>
        /// <param name="instanceId">Instance ID of the orchestration.</param>
        /// <param name="allExecutions">This parameter is not used.</param>
        /// <returns>List of <see cref="OrchestrationState"/> objects that represent the list of orchestrations.</returns>
        public async Task<IList<OrchestrationState>> GetOrchestrationStateAsync(string instanceId, bool allExecutions)
        {
            // Client operations will auto-create the task hub if it doesn't already exist.
            await this.EnsureTaskHubAsync();
            return await this.trackingStore.GetStateAsync(instanceId, allExecutions, fetchInput: true);
        }

        /// <summary>
        /// Get a the state of the specified execution (generation) of the specified orchestration instance.
        /// </summary>
        /// <param name="instanceId">Instance ID of the orchestration.</param>
        /// <param name="executionId">The execution ID (generation) of the specified instance.</param>
        /// <returns>The <see cref="OrchestrationState"/> object that represents the orchestration.</returns>
        public async Task<OrchestrationState> GetOrchestrationStateAsync(string instanceId, string executionId)
        {
            // Client operations will auto-create the task hub if it doesn't already exist.
            await this.EnsureTaskHubAsync();
            return await this.trackingStore.GetStateAsync(instanceId, executionId, fetchInput: true);
        }

        /// <summary>
        /// Get the most current execution (generation) of the specified instance.
        /// This method is not part of the IOrchestrationServiceClient interface. 
        /// </summary>
        /// <param name="instanceId">Instance ID of the orchestration.</param>
        /// <param name="allExecutions">This parameter is not used.</param>
        /// <param name="fetchInput">If set, fetch and return the input for the orchestration instance.</param>
        /// <returns>List of <see cref="OrchestrationState"/> objects that represent the list of orchestrations.</returns>
        public async Task<IList<OrchestrationState>> GetOrchestrationStateAsync(string instanceId, bool allExecutions, bool fetchInput = true)
        {
            // Client operations will auto-create the task hub if it doesn't already exist.
            await this.EnsureTaskHubAsync();
            return await this.trackingStore.GetStateAsync(instanceId, allExecutions, fetchInput);
        }

        /// <summary>
        /// Gets the state of all orchestration instances.
        /// </summary>
        /// <returns>List of <see cref="OrchestrationState"/></returns>
        public async Task<IList<OrchestrationState>> GetOrchestrationStateAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            await this.EnsureTaskHubAsync();
            return await this.trackingStore.GetStateAsync(cancellationToken);
        }

        /// <summary>
        /// Gets the state of all orchestration instances that match the specified parameters.
        /// </summary>
        /// <param name="createdTimeFrom">CreatedTime of orchestrations. Fetch status grater than this value.</param>
        /// <param name="createdTimeTo">CreatedTime of orchestrations. Fetch status less than this value.</param>
        /// <param name="runtimeStatus">RuntimeStatus of orchestrations. You can specify several status.</param>
        /// <param name="cancellationToken">Cancellation Token</param>
        /// <returns>List of <see cref="OrchestrationState"/></returns>
        public async Task<IList<OrchestrationState>> GetOrchestrationStateAsync(DateTime createdTimeFrom, DateTime? createdTimeTo, IEnumerable<OrchestrationStatus> runtimeStatus, CancellationToken cancellationToken = default(CancellationToken))
        {
            await this.EnsureTaskHubAsync();
            return await this.trackingStore.GetStateAsync(createdTimeFrom, createdTimeTo, runtimeStatus, cancellationToken);
        }

        /// <summary>
        /// Gets the state of all orchestration instances that match the specified parameters.
        /// </summary>
        /// <param name="createdTimeFrom">CreatedTime of orchestrations. Fetch status grater than this value.</param>
        /// <param name="createdTimeTo">CreatedTime of orchestrations. Fetch status less than this value.</param>
        /// <param name="runtimeStatus">RuntimeStatus of orchestrations. You can specify several status.</param>
        /// <param name="top">Top is number of records per one request.</param>
        /// <param name="continuationToken">ContinuationToken of the pager.</param>
        /// <param name="cancellationToken">Cancellation Token</param>
        /// <returns>List of <see cref="OrchestrationState"/></returns>
        public async Task<DurableStatusQueryResult> GetOrchestrationStateAsync(DateTime createdTimeFrom, DateTime? createdTimeTo, IEnumerable<OrchestrationStatus> runtimeStatus, int top, string continuationToken, CancellationToken cancellationToken = default(CancellationToken))
        {
            await this.EnsureTaskHubAsync();
            return await this.trackingStore.GetStateAsync(createdTimeFrom, createdTimeTo, runtimeStatus, top, continuationToken, cancellationToken);
        }

        /// <summary>
        /// Gets the state of all orchestration instances that match the specified parameters.
        /// </summary>
        /// <param name="condition">Query condition. <see cref="OrchestrationInstanceStatusQueryCondition"/></param>
        /// <param name="top">Top is number of records per one request.</param>
        /// <param name="continuationToken">ContinuationToken of the pager.</param>
        /// <param name="cancellationToken">Cancellation Token</param>
        /// <returns>List of <see cref="OrchestrationState"/></returns>
        public async Task<DurableStatusQueryResult> GetOrchestrationStateAsync(OrchestrationInstanceStatusQueryCondition condition, int top, string continuationToken, CancellationToken cancellationToken = default(CancellationToken))
        {
            await this.EnsureTaskHubAsync();
            return await this.trackingStore.GetStateAsync(condition, top, continuationToken, cancellationToken);
        }

        /// <summary>
        /// Force terminates an orchestration by sending a execution terminated event
        /// </summary>
        /// <param name="instanceId">Instance ID of the orchestration to terminate.</param>
        /// <param name="reason">The user-friendly reason for terminating.</param>
        public Task ForceTerminateTaskOrchestrationAsync(string instanceId, string reason)
        {
            var taskMessage = new TaskMessage
            {
                OrchestrationInstance = new OrchestrationInstance { InstanceId = instanceId },
                Event = new ExecutionTerminatedEvent(-1, reason)
            };

            return SendTaskOrchestrationMessageAsync(taskMessage);
        }

        /// <summary>
        /// Rewinds an orchestration then revives it from rewound state with a generic event message.
        /// </summary>
        /// <param name="instanceId">Instance ID of the orchestration to rewind.</param>
        /// <param name="reason">The reason for rewinding.</param>
        public async Task RewindTaskOrchestrationAsync(string instanceId, string reason)
        {
            var queueIds = await this.trackingStore.RewindHistoryAsync(instanceId, new List<string>(), default(CancellationToken));

            foreach (string id in queueIds)
            {
                var orchestrationInstance = new OrchestrationInstance
                {
                    InstanceId = id
                };

                var startedEvent = new GenericEvent(-1, reason);
                var taskMessage = new TaskMessage
                {
                    OrchestrationInstance = orchestrationInstance,
                    Event = startedEvent
                };

                await SendTaskOrchestrationMessageAsync(taskMessage);
            }
        }

        /// <summary>
        /// Get a string dump of the execution history of the specified execution (generation) of the specified instance.
        /// </summary>
        /// <param name="instanceId">Instance ID of the orchestration.</param>
        /// <param name="executionId">The execution ID (generation) of the specified instance.</param>
        /// <returns>String with formatted JSON array representing the execution history.</returns>
        public async Task<string> GetOrchestrationHistoryAsync(string instanceId, string executionId)
        {
            OrchestrationHistory history = await this.trackingStore.GetHistoryEventsAsync(
                instanceId,
                executionId,
                CancellationToken.None);
            return JsonConvert.SerializeObject(history.Events);
        }

        /// <summary>
        /// Purge history for an orchestration with a specified instance id.
        /// </summary>
        /// <param name="instanceId">Instance ID of the orchestration.</param>
        /// <returns>Class containing number of storage requests sent, along with instances and rows deleted/purged</returns>
        public Task<PurgeHistoryResult> PurgeInstanceHistoryAsync(string instanceId)
        {
            return this.trackingStore.PurgeInstanceHistoryAsync(instanceId);
        }

        /// <summary>
        /// Purge history for orchestrations that match the specified parameters.
        /// </summary>
        /// <param name="createdTimeFrom">CreatedTime of orchestrations. Purges history grater than this value.</param>
        /// <param name="createdTimeTo">CreatedTime of orchestrations. Purges history less than this value.</param>
        /// <param name="runtimeStatus">RuntimeStatus of orchestrations. You can specify several status.</param>
        /// <returns>Class containing number of storage requests sent, along with instances and rows deleted/purged</returns>
        public Task<PurgeHistoryResult> PurgeInstanceHistoryAsync(DateTime createdTimeFrom, DateTime? createdTimeTo, IEnumerable<OrchestrationStatus> runtimeStatus)
        {
            return this.trackingStore.PurgeInstanceHistoryAsync(createdTimeFrom, createdTimeTo, runtimeStatus);
        }

        /// <summary>
        /// Wait for an orchestration to reach any terminal state within the given timeout
        /// </summary>
        /// <param name="instanceId">The orchestration instance to wait for.</param>
        /// <param name="executionId">The execution ID (generation) of the specified instance.</param>
        /// <param name="timeout">Max timeout to wait.</param>
        /// <param name="cancellationToken">Task cancellation token.</param>
        public async Task<OrchestrationState> WaitForOrchestrationAsync(
            string instanceId,
            string executionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                throw new ArgumentException(nameof(instanceId));
            }

            TimeSpan statusPollingInterval = TimeSpan.FromSeconds(2);
            while (!cancellationToken.IsCancellationRequested && timeout > TimeSpan.Zero)
            {
                OrchestrationState state = await this.GetOrchestrationStateAsync(instanceId, executionId);
                if (state == null ||
                    state.OrchestrationStatus == OrchestrationStatus.Running ||
                    state.OrchestrationStatus == OrchestrationStatus.Pending ||
                    state.OrchestrationStatus == OrchestrationStatus.ContinuedAsNew)
                {
                    await Task.Delay(statusPollingInterval, cancellationToken);
                    timeout -= statusPollingInterval;
                }
                else
                {
                    if (this.settings.FetchLargeMessageDataEnabled)
                    {
                        state.Input = await this.messageManager.FetchLargeMessageIfNecessary(state.Input);
                        state.Output = await this.messageManager.FetchLargeMessageIfNecessary(state.Output);
                    }
                    return state;
                }
            }

            return null;
        }

        /// <summary>
        /// Purges orchestration instance state and history for orchestrations older than the specified threshold time.
        /// Also purges the blob storage. Currently only supported if a custom Instance store is provided.
        /// </summary>
        /// <param name="thresholdDateTimeUtc">Threshold date time in UTC</param>
        /// <param name="timeRangeFilterType">What to compare the threshold date time against</param>
        public Task PurgeOrchestrationHistoryAsync(DateTime thresholdDateTimeUtc, OrchestrationStateTimeRangeFilterType timeRangeFilterType)
        {
            return this.trackingStore.PurgeHistoryAsync(thresholdDateTimeUtc, timeRangeFilterType);
        }

        /// <summary>
        /// Download content that was too large to be stored in table storage,
        /// such as the input or output status fields, and for which the blob URI was stored instead.
        /// </summary>
        /// <param name="blobUri">The URI of the blob.</param>
        public Task<string> DownloadBlobAsync(string blobUri)
        {
            return this.messageManager.DownloadAndDecompressAsBytesAsync(new Uri(blobUri));
        }

        #endregion

        // TODO: Change this to a sticky assignment so that partition count changes can
        //       be supported: https://github.com/Azure/azure-functions-durable-extension/issues/1
        async Task<ControlQueue> GetControlQueueAsync(string instanceId)
        {
            uint partitionIndex = Fnv1aHashHelper.ComputeHash(instanceId) % (uint)this.settings.PartitionCount;
            CloudQueue storageQueue = GetControlQueue(this.queueClient, this.settings.TaskHubName, (int)partitionIndex);

            ControlQueue cachedQueue;
            if (!this.allControlQueues.TryGetValue(storageQueue.Name, out cachedQueue))
            {
                // Lock ensures all callers asking for the same partition get the same queue reference back.
                lock (this.allControlQueues)
                {
                    if (!this.allControlQueues.TryGetValue(storageQueue.Name, out cachedQueue))
                    {
                        cachedQueue = new ControlQueue(storageQueue, this.settings, this.stats, this.messageManager);
                        this.allControlQueues.TryAdd(storageQueue.Name, cachedQueue);
                    }
                }

                // Important to ensure the queue exists, whether the current thread initialized it or not.
                // A slightly better design would be to use a semaphore to block non-initializing threads.
                try
                {
                    await storageQueue.CreateIfNotExistsAsync();
                }
                finally
                {
                    this.stats.StorageRequests.Increment();
                }
            }

            System.Diagnostics.Debug.Assert(cachedQueue != null);
            return cachedQueue;
        }

        /// <summary>
        /// Disposes of the current object.
        /// </summary>
        public void Dispose()
        {
            this.orchestrationSessionManager.Dispose();
        }

        class PendingMessageBatch
        {
            public string OrchestrationInstanceId { get; set; }
            public string OrchestrationExecutionId { get; set; }

            public List<MessageData> Messages { get; set; } = new List<MessageData>();

            public OrchestrationRuntimeState Orchestrationstate { get; set; }
        }

        class ResettableLazy<T>
        {
            readonly Func<T> valueFactory;
            readonly LazyThreadSafetyMode threadSafetyMode;

            Lazy<T> lazy;

            public ResettableLazy(Func<T> valueFactory, LazyThreadSafetyMode mode)
            {
                this.valueFactory = valueFactory;
                this.threadSafetyMode = mode;

                this.Reset();
            }

            public T Value => this.lazy.Value;

            public void Reset()
            {
                this.lazy = new Lazy<T>(this.valueFactory, this.threadSafetyMode);
            }
        }

        struct QueueMessage
        {
            public QueueMessage(TaskHubQueue queue, TaskMessage message)
            {
                this.Queue = queue;
                this.Message = message;
            }

            public TaskHubQueue Queue { get; }
            public TaskMessage Message { get; }
        }
    }
}
