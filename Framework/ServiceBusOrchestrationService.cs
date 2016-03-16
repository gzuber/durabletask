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

namespace DurableTask
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Transactions;

    using DurableTask.Common;
    using DurableTask.History;
    using DurableTask.Tracing;
    using DurableTask.Tracking;
    using DurableTask.Serializing;
    using DurableTask.Settings;
    using Microsoft.ServiceBus;
    using Microsoft.ServiceBus.Messaging;

    public class ServiceBusOrchestrationService : IOrchestrationService, IOrchestrationServiceClient
    {
        // todo : move to settings
        const int PrefetchCount = 50;
        const int IntervalBetweenRetriesSecs = 5;
        const int MaxRetries = 5;
        // This is the Max number of messages which can be processed in a single transaction.
        // Current ServiceBus limit is 100 so it has to be lower than that.  
        // This also has an impact on prefetch count as PrefetchCount cannot be greater than this value
        // as every fetched message also creates a tracking message which counts towards this limit.
        const int MaxMessageCount = 80;
        const int SessionStreamWarningSizeInBytes = 200 * 1024;
        const int SessionStreamTerminationThresholdInBytes = 230 * 1024;
        const int StatusPollingIntervalInSeconds = 2;

        static readonly DataConverter DataConverter = new JsonDataConverter();
        readonly string connectionString;
        readonly string hubName;
        readonly ServiceBusOrchestrationServiceSettings settings;
        readonly MessagingFactory messagingFactory;
        QueueClient workerQueueClient;
        MessageSender orchestratorSender;
        QueueClient orchestratorQueueClient;
        MessageSender workerSender;
        QueueClient trackingQueueClient;
        MessageSender trackingSender;
        readonly string workerEntityName;
        readonly string orchestratorEntityName;
        readonly string trackingEntityName;
        WorkItemDispatcher<TrackingWorkItem> trackingDispatcher;
        IOrchestrationServiceInstanceStore instanceStore;

        // TODO : Make user of these instead of passing around the object references.
        // ConcurrentDictionary<string, ServiceBusOrchestrationSession> orchestrationSessions;
        // ConcurrentDictionary<string, MessageSession> orchestrationMessages;

        /// <summary>
        ///     Create a new ServiceBusOrchestrationService to the given service bus connection string and hubname
        /// </summary>
        /// <param name="connectionString">Service Bus connection string</param>
        /// <param name="hubName">Hubname to use with the connection string</param>
        /// <param name="instanceStore">History Provider, when supplied history messages will be tracked</param>
        /// <param name="settings">Settings object for service and client</param>
        public ServiceBusOrchestrationService(
            string connectionString, 
            string hubName,
            IOrchestrationServiceInstanceStore instanceStore,
            ServiceBusOrchestrationServiceSettings settings)
        {
            this.connectionString = connectionString;
            this.hubName = hubName;
            messagingFactory = ServiceBusUtils.CreateMessagingFactory(connectionString);
            workerEntityName = string.Format(FrameworkConstants.WorkerEndpointFormat, this.hubName);
            orchestratorEntityName = string.Format(FrameworkConstants.OrchestratorEndpointFormat, this.hubName);
            trackingEntityName = string.Format(FrameworkConstants.TrackingEndpointFormat, this.hubName);
            this.settings = settings ?? new ServiceBusOrchestrationServiceSettings();
            if (instanceStore != null)
            {
                this.instanceStore = instanceStore;
                trackingDispatcher = new WorkItemDispatcher<TrackingWorkItem>(
                    "TrackingDispatcher",
                    item => item == null ? string.Empty : item.InstanceId,
                    this.FetchTrackingWorkItemAsync,
                    this.ProcessTrackingWorkItemAsync)
                {
                    GetDelayInSecondsAfterOnFetchException = GetDelayInSecondsAfterOnFetchException,
                    GetDelayInSecondsAfterOnProcessException = GetDelayInSecondsAfterOnProcessException
                };
            }
        }

        public async Task StartAsync()
        {
            orchestratorSender = await messagingFactory.CreateMessageSenderAsync(orchestratorEntityName, workerEntityName);
            workerSender = await messagingFactory.CreateMessageSenderAsync(workerEntityName, orchestratorEntityName);
            trackingSender = await messagingFactory.CreateMessageSenderAsync(trackingEntityName, orchestratorEntityName);
            workerQueueClient = messagingFactory.CreateQueueClient(workerEntityName);
            orchestratorQueueClient = messagingFactory.CreateQueueClient(orchestratorEntityName);
            trackingQueueClient = messagingFactory.CreateQueueClient(trackingEntityName);
            if (trackingDispatcher != null)
            {
                await trackingDispatcher.StartAsync();
            }
        }

        public async Task StopAsync()
        {
            await StopAsync(false);
        }

        public async Task StopAsync(bool isForced)
        {
            await Task.WhenAll(
                this.workerSender.CloseAsync(),
                this.orchestratorSender.CloseAsync(),
                this.trackingSender.CloseAsync(),
                this.orchestratorQueueClient.CloseAsync(),
                this.trackingQueueClient.CloseAsync(),
                this.workerQueueClient.CloseAsync()
                );
            if (this.trackingDispatcher != null)
            {
                await this.trackingDispatcher.StopAsync(isForced);
            }

            await this.messagingFactory.CloseAsync();
        }

        public Task CreateAsync()
        {
            throw new NotImplementedException();
        }

        public async Task CreateIfNotExistsAsync()
        {
            NamespaceManager namespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);

            await Task.WhenAll(
                SafeDeleteQueueAsync(namespaceManager, orchestratorEntityName),
                SafeDeleteQueueAsync(namespaceManager, workerEntityName)
                );
            if (instanceStore != null)
            {
                await SafeDeleteQueueAsync(namespaceManager, trackingEntityName);
            }


            // TODO : siport : pull MaxDeliveryCount from settings
            await Task.WhenAll(
                CreateQueueAsync(namespaceManager, orchestratorEntityName, true, FrameworkConstants.MaxDeliveryCount),
                CreateQueueAsync(namespaceManager, workerEntityName, false, FrameworkConstants.MaxDeliveryCount)
                );
            if (instanceStore != null)
            {
                await CreateQueueAsync(namespaceManager, trackingEntityName, true, FrameworkConstants.MaxDeliveryCount);
            }

            // TODO : backward compat, add support for createInstanceStore flag 
            if (instanceStore != null)
            {
                await instanceStore.InitializeStorage(true);
            }
        }

        public Task DeleteAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>
        ///     Checks the message count against the threshold to see if a limit is being exceeded
        /// </summary>
        /// <param name="currentMessageCount">The current message count to check</param>
        /// <param name="runtimeState">The Orchestration runtime state this message count is associated with</param>
        public bool IsMaxMessageCountExceeded(int currentMessageCount, OrchestrationRuntimeState runtimeState)
        {
            return currentMessageCount
                + ((instanceStore != null) ? runtimeState.NewEvents.Count + 1 : 0) // one history message per new message + 1 for the orchestration
                > MaxMessageCount;
        }

        public int GetDelayInSecondsAfterOnProcessException(Exception exception)
        {
            if (IsTransientException(exception))
            {
                return settings.TaskOrchestrationDispatcherSettings.TransientErrorBackOffSecs;
            }

            return 0;
        }

        public int GetDelayInSecondsAfterOnFetchException(Exception exception)
        {
            if (exception is TimeoutException)
            {
                return 0;
            }

            int delay = settings.TaskOrchestrationDispatcherSettings.NonTransientErrorBackOffSecs;
            if (IsTransientException(exception))
            {
                delay = settings.TaskOrchestrationDispatcherSettings.TransientErrorBackOffSecs;
            }

            return delay;
        }

        public int MaxConcurrentTaskOrchestrationWorkItems => this.settings.TaskOrchestrationDispatcherSettings.MaxConcurrentOrchestrations;

        /// <summary>
        ///     Wait for the next orchestration work item and return the orchestration work item
        /// </summary>
        /// <param name="receiveTimeout">The timespan to wait for new messages before timing out</param>
        /// <param name="cancellationToken">The cancellation token to cancel execution of the task</param>
        public async Task<TaskOrchestrationWorkItem> LockNextTaskOrchestrationWorkItemAsync(TimeSpan receiveTimeout, CancellationToken cancellationToken)
        {
            //todo: should timeout be a param or shout if belong to the implementation
            MessageSession session = await orchestratorQueueClient.AcceptMessageSessionAsync(receiveTimeout);

            if (session == null)
            {
                return null;
            }

            // TODO : Here an elsewhere, consider standard rerty block instead of our own hand rolled version
            IList<BrokeredMessage> newMessages =
                (await Utils.ExecuteWithRetries(() => session.ReceiveBatchAsync(PrefetchCount),
                    session.SessionId, "Receive Session Message Batch", MaxRetries, IntervalBetweenRetriesSecs)).ToList();

            // TODO: use getformattedlog equiv / standardize log format
            TraceHelper.TraceSession(TraceEventType.Information, session.SessionId,
                $"{newMessages.Count()} new messages to process: {string.Join(",", newMessages.Select(m => m.MessageId))}");

            IList<TaskMessage> newTaskMessages = await Task.WhenAll(
                newMessages.Select(async message => await ServiceBusUtils.GetObjectFromBrokeredMessageAsync<TaskMessage>(message)));

            OrchestrationRuntimeState runtimeState = await GetSessionState(session);

            var lockTokens = newMessages.ToDictionary(m => m.LockToken, m => m);
            var sessionState = new ServiceBusOrchestrationSession
            {
                Session = session,
                LockTokens = lockTokens
            };

            return new TaskOrchestrationWorkItem
            {
                InstanceId = session.SessionId,
                LockedUntilUtc = session.LockedUntilUtc,
                NewMessages = newTaskMessages,
                OrchestrationRuntimeState = runtimeState,
                SessionInstance = sessionState
            };
        }

        /// <summary>
        ///     Renew the lock on an orchestration
        /// </summary>
        /// <param name="workItem">The task orchestration to renew the lock on</param>
        public async Task RenewTaskOrchestrationWorkItemLockAsync(TaskOrchestrationWorkItem workItem)
        {
            var sessionState = workItem?.SessionInstance as ServiceBusOrchestrationSession;
            if (sessionState?.Session == null)
            {
                return;
            }

            TraceHelper.TraceSession(TraceEventType.Error, workItem.InstanceId, "Renew lock on orchestration session");
            await sessionState.Session.RenewLockAsync();
            workItem.LockedUntilUtc = sessionState.Session.LockedUntilUtc;
        }

        /// <summary>
        ///     Complete an orchestation, this atomically sends any outbound messages and completes the session for all current messages
        /// </summary>
        /// <param name="workItem">The task orchestration to renew the lock on</param>
        /// <param name="newOrchestrationRuntimeState">New state of the orchestration to be persisted</param>
        /// <param name="outboundMessages">New work item messages to be processed</param>
        /// <param name="orchestratorMessages">New orchestration messages to be scheduled</param>
        /// <param name="timerMessages">Delayed exection messages to be scheduled for the orchestration</param>
        /// <param name="continuedAsNewMessage">Task Message to send to orchestrator queue to treat as new in order to rebuild state</param>
        /// <param name="orchestrationState">The prior orchestration state</param>
        public async Task CompleteTaskOrchestrationWorkItemAsync(
            TaskOrchestrationWorkItem workItem,
            OrchestrationRuntimeState newOrchestrationRuntimeState, 
            IList<TaskMessage> outboundMessages, 
            IList<TaskMessage> orchestratorMessages,
            IList<TaskMessage> timerMessages,
            TaskMessage continuedAsNewMessage,
            OrchestrationState orchestrationState)
        {

            var runtimeState = workItem.OrchestrationRuntimeState;
            var sessionState = workItem.SessionInstance as ServiceBusOrchestrationSession;
            if (sessionState == null)
            {
                // todo : figure out what this means, its a terminal error for the orchestration
                throw new ArgumentNullException("SessionInstance");
            }

            var session = sessionState.Session;

            using (var ts = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                if (await TrySetSessionState(workItem, newOrchestrationRuntimeState, runtimeState, session))
                {
                    if (runtimeState.CompressedSize > SessionStreamWarningSizeInBytes)
                    {
                        TraceHelper.TraceSession(TraceEventType.Error, workItem.InstanceId, "Size of session state is nearing session size limit of 256KB");
                    }

                    // We need to .ToList() the IEnumerable otherwise GetBrokeredMessageFromObject gets called 5 times per message due to Service Bus doing multiple enumeration
                    if (outboundMessages?.Count > 0)
                    {
                        await workerSender.SendBatchAsync(
                            outboundMessages.Select(m => 
                            ServiceBusUtils.GetBrokeredMessageFromObject(
                                m, 
                                settings.MessageCompressionSettings, 
                                null, 
                                "Worker outbound message"))
                            .ToList()
                            );
                    }

                    if (timerMessages?.Count > 0)
                    {
                        await orchestratorQueueClient.SendBatchAsync(
                            timerMessages.Select(m =>
                            {
                                BrokeredMessage message = ServiceBusUtils.GetBrokeredMessageFromObject(
                                m,
                                settings.MessageCompressionSettings,
                                newOrchestrationRuntimeState.OrchestrationInstance,
                                "Timer Message");
                                message.ScheduledEnqueueTimeUtc = ((TimerFiredEvent)m.Event).FireAt;
                                return message;
                            })
                            .ToList()
                            );
                    }

                    if (orchestratorMessages?.Count > 0)
                    {
                        await orchestratorQueueClient.SendBatchAsync(
                            orchestratorMessages.Select(m => 
                            ServiceBusUtils.GetBrokeredMessageFromObject(
                                m, 
                                settings.MessageCompressionSettings, 
                                m.OrchestrationInstance, 
                                "Sub Orchestration"))
                            .ToList()
                            );
                    }

                    if (continuedAsNewMessage != null)
                    {
                        await orchestratorQueueClient.SendAsync(
                            ServiceBusUtils.GetBrokeredMessageFromObject(
                                continuedAsNewMessage, 
                                settings.MessageCompressionSettings, 
                                newOrchestrationRuntimeState.OrchestrationInstance, 
                                "Continue as new")
                            );
                    }

                    if (instanceStore != null)
                    {
                        List<BrokeredMessage> trackingMessages = CreateTrackingMessages(runtimeState);

                        TraceHelper.TraceInstance(TraceEventType.Information, runtimeState.OrchestrationInstance,
                            "Created {0} tracking messages", trackingMessages.Count);

                        if (trackingMessages.Count > 0)
                        {
                            await trackingSender.SendBatchAsync(trackingMessages);
                        }
                    }
                }

                await session.CompleteBatchAsync(sessionState.LockTokens.Keys);
                ts.Complete();

                // This is just outside the transaction to avoid a possible race condition (e.g. a session got unlocked and then the txn was committed).
                await sessionState.Session.CloseAsync();
            }
        }

        /// <summary>
        ///     Release the lock on an orchestration, releases the session, decoupled from CompleteTaskOrchestrationWorkItemAsync to handle nested orchestrations
        /// </summary>
        /// <param name="workItem">The task orchestration to abandon</param>
        public Task ReleaseTaskOrchestrationWorkItemAsync(TaskOrchestrationWorkItem workItem)
        {
            return Task.FromResult(0); // This provider does not need to anything here
        }

        /// <summary>
        ///     Abandon an orchestation, this abandons ownership/locking of all messages for an orchestation and it's session
        /// </summary>
        /// <param name="workItem">The task orchestration to abandon</param>
        public async Task AbandonTaskOrchestrationWorkItemAsync(TaskOrchestrationWorkItem workItem)
        {
            var sessionState = workItem?.SessionInstance as ServiceBusOrchestrationSession;
            if (sessionState?.Session == null)
            {
                return;
            }

            TraceHelper.TraceSession(TraceEventType.Error, workItem.InstanceId, "Abandoning {0} messages due to workitem abort", sessionState.LockTokens.Keys.Count());
            foreach (var lockToken in sessionState.LockTokens.Keys)
            {
                await sessionState.Session.AbandonAsync(lockToken);
            }

            try
            {
                sessionState.Session.Abort();
            }
            catch (Exception ex) when (!Utils.IsFatal(ex))
            {
                TraceHelper.TraceExceptionSession(TraceEventType.Warning, workItem.InstanceId, ex, "Error while aborting session");
            }
        }


        public int MaxConcurrentTaskActivityWorkItems => this.settings.TaskActivityDispatcherSettings.MaxConcurrentActivities;

        /// <summary>
        ///    Wait for an lock the next task activity to be processed 
        /// </summary>
        /// <param name="receiveTimeout">The timespan to wait for new messages before timing out</param>
        /// <param name="cancellationToken">The cancellation token to cancel execution of the task</param>
        public async Task<TaskActivityWorkItem> LockNextTaskActivityWorkItem(TimeSpan receiveTimeout, CancellationToken cancellationToken)
        {
            BrokeredMessage receivedMessage = await workerQueueClient.ReceiveAsync(receiveTimeout);

            if (receivedMessage == null)
            {
                return null;
            }

            // TODO: use getformattedlog
            TraceHelper.TraceSession(TraceEventType.Information,
                receivedMessage.SessionId,
                $"New message to process: {receivedMessage.MessageId} [{receivedMessage.SequenceNumber}]");

            TaskMessage taskMessage = await ServiceBusUtils.GetObjectFromBrokeredMessageAsync<TaskMessage>(receivedMessage);

            return new TaskActivityWorkItem
            {
                Id = receivedMessage.MessageId,
                LockedUntilUtc = receivedMessage.LockedUntilUtc,
                TaskMessage = taskMessage,
                MessageState = receivedMessage
            };
        }

        /// <summary>
        ///    Renew the lock on a still processing work item
        /// </summary>
        /// <param name="workItem">Work item to renew the lock on</param>
        public async Task<TaskActivityWorkItem> RenewTaskActivityWorkItemLockAsync(TaskActivityWorkItem workItem)
        {
            var message = workItem?.MessageState as BrokeredMessage;
            if (message != null)
            {
                await message.RenewLockAsync();
                workItem.LockedUntilUtc = message.LockedUntilUtc;
            }

            return workItem;
        }

        /// <summary>
        ///    Atomically complete a work item and send the response messages
        /// </summary>
        /// <param name="workItem">Work item to complete</param>
        /// <param name="responseMessage">The response message to send</param>
        public async Task CompleteTaskActivityWorkItemAsync(TaskActivityWorkItem workItem, TaskMessage responseMessage)
        {
            BrokeredMessage brokeredResponseMessage = ServiceBusUtils.GetBrokeredMessageFromObject(
                responseMessage,
                settings.MessageCompressionSettings,
                workItem.TaskMessage.OrchestrationInstance, 
                $"Response for {workItem.TaskMessage.OrchestrationInstance.InstanceId}");

            var originalMessage = workItem.MessageState as BrokeredMessage;
            if (originalMessage == null)
            {
                //todo : figure out the correct action here, this is bad
                throw new ArgumentNullException("originalMessage");
            }

            using (var ts = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await Task.WhenAll(
                    workerQueueClient.CompleteAsync(originalMessage.LockToken),
                    orchestratorSender.SendAsync(brokeredResponseMessage));
                ts.Complete();
            }
        }

        /// <summary>
        ///    Abandons a single work item and releases the lock on it
        /// </summary>
        /// <param name="workItem">The work item to abandon</param>
        public Task AbandonTaskActivityWorkItemAsync(TaskActivityWorkItem workItem)
        {
            var message = workItem?.MessageState as BrokeredMessage;
            TraceHelper.Trace(TraceEventType.Information, $"Abandoning message {workItem?.Id}");
            return message?.AbandonAsync();
        }

        /// <summary>
        ///    Create/start a new Orchestration
        /// </summary>
        /// <param name="creationMessage">The task message for the new Orchestration</param>
        public async Task CreateTaskOrchestrationAsync(TaskMessage creationMessage)
        {
            // Keep this as its own message so create specific logic can run here like adding tracking information
            await SendTaskOrchestrationMessage(creationMessage);
        }

        /// <summary>
        ///    Send an orchestration message
        /// </summary>
        /// <param name="message">The task message to be sent for the orchestration</param>
        public async Task SendTaskOrchestrationMessage(TaskMessage message)
        {
            BrokeredMessage brokeredMessage = ServiceBusUtils.GetBrokeredMessageFromObject(
                message,
                this.settings.MessageCompressionSettings,
                message.OrchestrationInstance,
                "SendTaskOrchestrationMessage");

            MessageSender sender = await messagingFactory.CreateMessageSenderAsync(orchestratorEntityName).ConfigureAwait(false);
            await sender.SendAsync(brokeredMessage).ConfigureAwait(false);
            await sender.CloseAsync().ConfigureAwait(false);
        }

        public async Task ForceTerminateTaskOrchestrationAsync(string instanceId, string reason)
        {
            var taskMessage = new TaskMessage
            {
                OrchestrationInstance = new OrchestrationInstance { InstanceId = instanceId },
                Event = new ExecutionTerminatedEvent(-1, reason)
            };

            await SendTaskOrchestrationMessage(taskMessage);
        }

        /// <summary>
        ///     Wait for an orchestration to reach any terminal state within the given timeout
        /// </summary>
        /// <param name="instanceId">Instance to terminate</param>
        /// <param name="executionId">The execution id of the orchestration</param>
        /// <param name="timeout">Max timeout to wait</param>
        /// <param name="cancellationToken">Task cancellation token</param>
        public async Task<OrchestrationState> WaitForOrchestrationAsync(
            string instanceId, 
            string executionId, 
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ThrowIfInstanceStoreNotConfigured();

            if (string.IsNullOrWhiteSpace(instanceId))
            {
                throw new ArgumentException("instanceId");
            }

            var timeoutSeconds = timeout.TotalSeconds;

            while (timeoutSeconds > 0)
            {
                OrchestrationState state = await GetOrchestrationStateAsync(instanceId, executionId);
                if (state == null || (state.OrchestrationStatus == OrchestrationStatus.Running))
                {
                    await Task.Delay(StatusPollingIntervalInSeconds * 1000, cancellationToken);
                    timeoutSeconds -= StatusPollingIntervalInSeconds;
                }
                else
                {
                    return state;
                }
            }

            return null;
        }

        public async Task<IList<OrchestrationState>> GetOrchestrationStateAsync(string instanceId, bool allExecutions)
        {
            ThrowIfInstanceStoreNotConfigured();
            IEnumerable<OrchestrationStateHistoryEvent> states = await instanceStore.GetOrchestrationStateAsync(instanceId, allExecutions);
            return states?.Select(s => s.State).ToList();
        }

        public async Task<OrchestrationState> GetOrchestrationStateAsync(string instanceId, string executionId)
        {
            ThrowIfInstanceStoreNotConfigured();
            OrchestrationStateHistoryEvent state = await instanceStore.GetOrchestrationStateAsync(instanceId, executionId);
            return state?.State;
        }

        public async Task<string> GetOrchestrationHistoryAsync(string instanceId, string executionId)
        {
            ThrowIfInstanceStoreNotConfigured();
            IEnumerable<OrchestrationWorkItemEvent> historyEvents =
                await instanceStore.GetOrchestrationHistoryEventsAsync(instanceId, executionId);

            return DataConverter.Serialize(historyEvents.Select(historyEventEntity => historyEventEntity.HistoryEvent));
        }

        public async Task PurgeOrchestrationInstanceHistoryAsync(
            DateTime thresholdDateTimeUtc,
            OrchestrationStateTimeRangeFilterType timeRangeFilterType)
        {
            ThrowIfInstanceStoreNotConfigured();

            TraceHelper.Trace(TraceEventType.Information, $"Purging orchestration instances before: {thresholdDateTimeUtc}, Type: {timeRangeFilterType}");

            int purgedEvents = await instanceStore.PurgeOrchestrationHistoryEventsAsync(thresholdDateTimeUtc, timeRangeFilterType);

            TraceHelper.Trace(TraceEventType.Information, $"Purged {purgedEvents} orchestration histories");
        }

        /// <summary>
        ///     Inspect an exception to see if it's a transient exception that may apply different delays
        /// </summary>
        /// <param name="exception">Exception to inspect</param>
        static bool IsTransientException(Exception exception)
        {
            return (exception as MessagingException)?.IsTransient ?? false;
        }

        /// <summary>
        ///     Wait for the next orchestration work item and return the orchestration work item
        /// </summary>
        /// <param name="receiveTimeout">The timespan to wait for new messages before timing out</param>
        async Task<TrackingWorkItem> FetchTrackingWorkItemAsync(TimeSpan receiveTimeout)
        {
            //todo: this method should use a more generic method to avoid the dupe code
            MessageSession session = await trackingQueueClient.AcceptMessageSessionAsync(receiveTimeout);

            if (session == null)
            {
                return null;
            }

            IList<BrokeredMessage> newMessages =
                (await Utils.ExecuteWithRetries(() => session.ReceiveBatchAsync(PrefetchCount),
                    session.SessionId, "Receive Tracking Session Message Batch", MaxRetries, IntervalBetweenRetriesSecs)).ToList();

            // TODO: use getformattedlog equiv / standardize log format
            TraceHelper.TraceSession(TraceEventType.Information, session.SessionId,
                $"{newMessages.Count()} new tracking messages to process: {string.Join(",", newMessages.Select(m => m.MessageId))}");

            IList<TaskMessage> newTaskMessages = await Task.WhenAll(
                newMessages.Select(async message => await ServiceBusUtils.GetObjectFromBrokeredMessageAsync<TaskMessage>(message)));

            var lockTokens = newMessages.ToDictionary(m => m.LockToken, m => m);
            var sessionState = new ServiceBusOrchestrationSession
            {
                Session = session,
                LockTokens = lockTokens
            };

            return new TrackingWorkItem
            {
                InstanceId = session.SessionId,
                LockedUntilUtc = session.LockedUntilUtc,
                NewMessages = newTaskMessages,
                SessionInstance = sessionState
            };
        }

        List<BrokeredMessage> CreateTrackingMessages(OrchestrationRuntimeState runtimeState)
        {
            // TODO : This method outputs BrokeredMessage, this avoids the packing of the messages in the calling method but makes these strongly to the the transport
            var trackingMessages = new List<BrokeredMessage>();

            // We cannot create tracking messages if runtime state does not have Orchestration InstanceId
            // This situation can happen due to corruption of service bus session state or if somehow first message of orchestration is not execution started
            if (string.IsNullOrWhiteSpace(runtimeState?.OrchestrationInstance?.InstanceId))
            {
                return trackingMessages;
            }

            // this is to stamp the tracking events with a sequence number so they can be ordered even if
            // writing to a store like azure table
            int historyEventIndex = runtimeState.Events.Count - runtimeState.NewEvents.Count;
            foreach (HistoryEvent he in runtimeState.NewEvents)
            {
                var taskMessage = new TaskMessage
                {
                    Event = he,
                    SequenceNumber = historyEventIndex++,
                    OrchestrationInstance = runtimeState.OrchestrationInstance
                };

                BrokeredMessage trackingMessage = ServiceBusUtils.GetBrokeredMessageFromObject(
                    taskMessage, 
                    settings.MessageCompressionSettings, 
                    runtimeState.OrchestrationInstance,
                    "History Tracking Message");
                trackingMessages.Add(trackingMessage);
            }

            var stateMessage = new TaskMessage
            {
                Event = new HistoryStateEvent(-1, Utils.BuildOrchestrationState(runtimeState)),
                OrchestrationInstance = runtimeState.OrchestrationInstance
            };

            BrokeredMessage brokeredStateMessage = ServiceBusUtils.GetBrokeredMessageFromObject(
                stateMessage,
                settings.MessageCompressionSettings,
                runtimeState.OrchestrationInstance,
                "State Tracking Message");
            trackingMessages.Add(brokeredStateMessage);

            return trackingMessages;
        }

        /// <summary>
        ///     Process a tracking work item, sending to storage and releasing
        /// </summary>
        /// <param name="workItem">The tracking work item to process</param>
        async Task ProcessTrackingWorkItemAsync(TrackingWorkItem workItem)
        {

            var sessionState = workItem.SessionInstance as ServiceBusOrchestrationSession;
            if (sessionState == null)
            {
                // todo : figure out what this means, its a terminal error for the orchestration
                throw new ArgumentNullException("SessionInstance");
            }


            var historyEntities = new List<OrchestrationWorkItemEvent>();
            var stateEntities = new List<OrchestrationStateHistoryEvent>();

            foreach (TaskMessage taskMessage in workItem.NewMessages)
            {
                if (taskMessage.Event.EventType == EventType.HistoryState)
                {
                    stateEntities.Add(new OrchestrationStateHistoryEvent
                    {
                        State = (taskMessage.Event as HistoryStateEvent)?.State
                    });
                }
                else
                {
                    historyEntities.Add(new OrchestrationWorkItemEvent
                    {
                        InstanceId = taskMessage.OrchestrationInstance.InstanceId,
                        ExecutionId = taskMessage.OrchestrationInstance.ExecutionId,
                        SequenceNumber = (int) taskMessage.SequenceNumber,
                        EventTimestamp = DateTime.UtcNow,
                        HistoryEvent = taskMessage.Event
                    });
                }
            }

            TraceEntities(TraceEventType.Verbose, "Writing tracking history event", historyEntities, GetNormalizedWorkItemEvent);
            TraceEntities(TraceEventType.Verbose, "Writing tracking state event", stateEntities, GetNormalizedStateEvent);

            // TODO : pick a retry strategy, old code wrapped every individual item in a retry
            try
            {
                await instanceStore.WriteEntitesAsync(historyEntities);
            }
            catch (Exception e) when (!Utils.IsFatal(e))
            {
                TraceEntities(TraceEventType.Critical, "Failed to write history entity", historyEntities, GetNormalizedWorkItemEvent);
                throw;
            }

            try
            {
                // TODO : Original code has this as a loop, why not just a batch??
                foreach (OrchestrationStateHistoryEvent stateEntity in stateEntities)
                {
                    await instanceStore.WriteEntitesAsync(new List<OrchestrationStateHistoryEvent> { stateEntity });
                }
            }
            catch (Exception e) when (!Utils.IsFatal(e))
            {
                TraceEntities(TraceEventType.Critical, "Failed to write history entity", stateEntities, GetNormalizedStateEvent);
                throw;
            }

            // Cleanup our session
            await sessionState.Session.CompleteBatchAsync(sessionState.LockTokens.Keys);
            await sessionState.Session.CloseAsync();
        }

        void TraceEntities<T>(
            TraceEventType eventType, 
            string message,
            IEnumerable<T> entities, 
            Func<int, string, T, string> traceGenerator)
        {
            int index = 0;
            foreach (T entry in entities)
            {
                var idx = index;
                TraceHelper.Trace(eventType, () => traceGenerator(idx, message, entry));
                index++;
            }
        }

        string GetNormalizedStateEvent(int index, string message, OrchestrationStateHistoryEvent stateEntity)
        {
            string serializedHistoryEvent = Utils.EscapeJson(DataConverter.Serialize(stateEntity.State));
            int historyEventLength = serializedHistoryEvent.Length;

            int maxLen = instanceStore?.MaxHistoryEntryLength?? int.MaxValue;

            if (historyEventLength > maxLen)
            {
                serializedHistoryEvent = serializedHistoryEvent.Substring(0, maxLen) + " ....(truncated)..]";
            }

            return GetFormattedLog(
                $"{message} - #{index} - Instance Id: {stateEntity.State?.OrchestrationInstance?.InstanceId},"
                + $" Execution Id: {stateEntity.State?.OrchestrationInstance?.ExecutionId},"
                + $" State Length: {historyEventLength}\n{serializedHistoryEvent}");
        }

        string GetNormalizedWorkItemEvent(int index, string message, OrchestrationWorkItemEvent entity)
        {
            string serializedHistoryEvent = Utils.EscapeJson(DataConverter.Serialize(entity.HistoryEvent));
            int historyEventLength = serializedHistoryEvent.Length;
            int maxLen = instanceStore?.MaxHistoryEntryLength?? int.MaxValue;

            if (historyEventLength > maxLen)
            {
                serializedHistoryEvent = serializedHistoryEvent.Substring(0, maxLen) + " ....(truncated)..]";
            }

            return GetFormattedLog(
                $"{message} - #{index} - Instance Id: {entity.InstanceId}, Execution Id: {entity.ExecutionId}, HistoryEvent Length: {historyEventLength}\n{serializedHistoryEvent}");
        }

        string GetFormattedLog(string input)
        {
            // todo : fill this method with the correct workitemdispatcher prefix
            return input;
        }

        static async Task<OrchestrationRuntimeState> GetSessionState(MessageSession session)
        {
            long rawSessionStateSize;
            long newSessionStateSize;
            OrchestrationRuntimeState runtimeState;

            using (Stream rawSessionStream = await session.GetStateAsync())
            using (Stream sessionStream = await Utils.GetDecompressedStreamAsync(rawSessionStream))
            {
                bool isEmptySession = sessionStream == null;
                rawSessionStateSize = isEmptySession ? 0 : rawSessionStream.Length;
                newSessionStateSize = isEmptySession ? 0 : sessionStream.Length;

                runtimeState = GetOrCreateInstanceState(sessionStream, session.SessionId);
            }

            TraceHelper.TraceSession(TraceEventType.Information, session.SessionId,
                $"Size of session state is {newSessionStateSize}, compressed {rawSessionStateSize}");

            return runtimeState;
        }

        async Task<bool> TrySetSessionState(
            TaskOrchestrationWorkItem workItem,
            OrchestrationRuntimeState newOrchestrationRuntimeState,
            OrchestrationRuntimeState runtimeState,
            MessageSession session)
        {
            bool isSessionSizeThresholdExceeded = false;

            if (newOrchestrationRuntimeState == null)
            {
                session.SetState(null);
                return true;
            }

            string serializedState = DataConverter.Serialize(newOrchestrationRuntimeState);

            long originalStreamSize = 0;
            using (
                Stream compressedState = Utils.WriteStringToStream(
                    serializedState,
                    settings.TaskOrchestrationDispatcherSettings.CompressOrchestrationState,
                    out originalStreamSize))
            {
                runtimeState.Size = originalStreamSize;
                runtimeState.CompressedSize = compressedState.Length;
                if (runtimeState.CompressedSize > SessionStreamTerminationThresholdInBytes)
                {
                    // basic idea is to simply enqueue a terminate message just like how we do it from taskhubclient
                    // it is possible to have other messages in front of the queue and those will get processed before
                    // the terminate message gets processed. but that is ok since in the worst case scenario we will 
                    // simply land in this if-block again and end up queuing up another terminate message.
                    //
                    // the interesting scenario is when the second time we *dont* land in this if-block because e.g.
                    // the new messages that we processed caused a new generation to be created. in that case
                    // it is still ok because the worst case scenario is that we will terminate a newly created generation
                    // which shouldn't have been created at all in the first place

                    isSessionSizeThresholdExceeded = true;

                    string reason = $"Session state size of {runtimeState.CompressedSize} exceeded the termination threshold of {SessionStreamTerminationThresholdInBytes} bytes";
                    TraceHelper.TraceSession(TraceEventType.Critical, workItem.InstanceId, reason);

                    BrokeredMessage forcedTerminateMessage = CreateForcedTerminateMessage(runtimeState.OrchestrationInstance.InstanceId, reason);

                    await orchestratorQueueClient.SendAsync(forcedTerminateMessage);
                }
                else
                {
                    session.SetState(compressedState);
                }
            }

            return !isSessionSizeThresholdExceeded;
        }

        static Task SafeDeleteQueueAsync(NamespaceManager namespaceManager, string path)
        {
            try
            {
                return namespaceManager.DeleteQueueAsync(path);
            }
            catch (MessagingEntityNotFoundException)
            {
                return Task.FromResult(0);
            }
        }

        Task SafeCreateQueueAsync(NamespaceManager namespaceManager, string path, bool requiresSessions,
            int maxDeliveryCount)
        {
            try
            {
                return CreateQueueAsync(namespaceManager, path, requiresSessions, maxDeliveryCount);
            }
            catch (MessagingEntityAlreadyExistsException)
            {
                return Task.FromResult(0);
            }
        }

        async Task CreateQueueAsync(
            NamespaceManager namespaceManager, 
            string path, 
            bool requiresSessions,
            int maxDeliveryCount)
        {
            var description = new QueueDescription(path)
            {
                RequiresSession = requiresSessions,
                MaxDeliveryCount = maxDeliveryCount
            };

            await namespaceManager.CreateQueueAsync(description);
        }

        BrokeredMessage CreateForcedTerminateMessage(string instanceId, string reason)
        {
            var newOrchestrationInstance = new OrchestrationInstance {InstanceId = instanceId};
            var taskMessage = new TaskMessage
            {
                OrchestrationInstance = newOrchestrationInstance,
                Event = new ExecutionTerminatedEvent(-1, reason)
            };

            BrokeredMessage message = ServiceBusUtils.GetBrokeredMessageFromObject(
                taskMessage,
                settings.MessageCompressionSettings, 
                newOrchestrationInstance,
                "Forced Terminate");

            return message;
        }

        static OrchestrationRuntimeState GetOrCreateInstanceState(Stream stateStream, string sessionId)
        {
            // todo : find the correct home for this method
            OrchestrationRuntimeState runtimeState;
            if (stateStream == null)
            {
                TraceHelper.TraceSession(TraceEventType.Information, sessionId,
                    "No session state exists, creating new session state.");
                runtimeState = new OrchestrationRuntimeState();
            }
            else
            {
                if (stateStream.Position != 0)
                {
                    throw TraceHelper.TraceExceptionSession(TraceEventType.Error, sessionId,
                        new ArgumentException("Stream is partially consumed"));
                }

                string serializedState = null;
                using (var reader = new StreamReader(stateStream))
                {
                    serializedState = reader.ReadToEnd();
                }

                OrchestrationRuntimeState restoredState = DataConverter.Deserialize<OrchestrationRuntimeState>(serializedState);
                // Create a new Object with just the events, we don't want the rest
                runtimeState = new OrchestrationRuntimeState(restoredState.Events);
            }

            return runtimeState;
        }

        void ThrowIfInstanceStoreNotConfigured()
        {
            if (instanceStore == null)
            {
                throw new InvalidOperationException("Instance store is not configured");
            }
        }
    }
}