using System.Buffers;
using System.Collections.Concurrent;
using NATS.Client.Core.Internal;

namespace NATS.Client.Core;

internal sealed class SubscriptionManager : IDisposable
{
#pragma warning disable SA1401
    internal readonly object Gate = new object(); // lock for add/remove, publish can avoid lock.
    internal readonly NatsConnection Connection;
#pragma warning restore SA1401

    private readonly ConcurrentDictionary<int, RefCountSubscription> _bySubscriptionId = new();
    private readonly ConcurrentDictionary<string, RefCountSubscription> _byStringKey = new();

    private int _subscriptionId = 0; // unique alphanumeric subscription ID, generated by the client(per connection).

    public SubscriptionManager(NatsConnection connection)
    {
        Connection = connection;
    }

    public (int subscriptionId, string subject, NatsKey? queueGroup)[] GetExistingSubscriptions()
    {
        lock (Gate)
        {
            return _bySubscriptionId.Select(x => (x.Value.SubscriptionId, x.Value.Key, x.Value.QueueGroup)).ToArray();
        }
    }

    public async ValueTask<IDisposable> AddAsync<T>(string key, NatsKey? queueGroup, Action<T> handler)
    {
        int sid;
        RefCountSubscription? subscription;
        int handlerId;

        lock (Gate)
        {
            if (_byStringKey.TryGetValue(key, out subscription))
            {
                if (subscription.ElementType != typeof(T))
                {
                    throw new InvalidOperationException($"Register different type on same key. Key: {key} RegisteredType:{subscription.ElementType.FullName} NewType:{typeof(T).FullName}");
                }

                handlerId = subscription.AddHandler(handler);
                return new Subscription(subscription, handlerId);
            }
            else
            {
                sid = Interlocked.Increment(ref _subscriptionId);

                subscription = new RefCountSubscription(this, sid, key, typeof(T))
                {
                    QueueGroup = queueGroup,
                };
                handlerId = subscription.AddHandler(handler);
                _bySubscriptionId[sid] = subscription;
                _byStringKey[key] = subscription;
            }
        }

        var returnSubscription = new Subscription(subscription, handlerId);
        try
        {
            await Connection.SubscribeAsync(sid, key, queueGroup).ConfigureAwait(false);
        }
        catch
        {
            returnSubscription.Dispose(); // can't subscribed, remove from holder.
            throw;
        }

        return returnSubscription;
    }

    public async ValueTask<IDisposable> AddRequestHandlerAsync<TRequest, TResponse>(string key, Func<TRequest, TResponse> handler)
    {
        int sid;
        RefCountSubscription? subscription;
        int handlerId;

        lock (Gate)
        {
            if (_byStringKey.TryGetValue(key, out subscription))
            {
                if (!subscription.IsRequestHandler)
                {
                    throw new InvalidOperationException($"Already registered not handler. Key: {key}.");
                }

                if (subscription.ElementType != typeof(TRequest) || subscription.ResponseType != typeof(TResponse))
                {
                    throw new InvalidOperationException($"Register different type on same key. Key: {key} RegisteredType: ({subscription.ElementType.FullName},{subscription.ResponseType!.FullName}) NewType: ({typeof(TRequest).FullName}, {typeof(TResponse).FullName}");
                }

                handlerId = subscription.AddHandler(handler);
                return new Subscription(subscription, handlerId);
            }
            else
            {
                sid = Interlocked.Increment(ref _subscriptionId);

                subscription = new RefCountSubscription(this, sid, key, typeof(TRequest), typeof(TResponse));
                handlerId = subscription.AddHandler(handler);
                _bySubscriptionId[sid] = subscription;
                _byStringKey[key] = subscription;
            }
        }

        var returnSubscription = new Subscription(subscription, handlerId);
        try
        {
            await Connection.SubscribeAsync(sid, key, null).ConfigureAwait(false);
        }
        catch
        {
            returnSubscription.Dispose(); // can't subscribed, remove from holder.
            throw;
        }

        return returnSubscription;
    }

    public async ValueTask<IDisposable> AddRequestHandlerAsync<TRequest, TResponse>(string key, Func<TRequest, Task<TResponse>> asyncHandler)
    {
        int sid;
        RefCountSubscription? subscription;
        int handlerId;

        lock (Gate)
        {
            if (_byStringKey.TryGetValue(key, out subscription))
            {
                if (!subscription.IsRequestHandler)
                {
                    throw new InvalidOperationException($"Already registered not handler. Key: {key}.");
                }

                if (subscription.ElementType != typeof(TRequest) || subscription.ResponseType != typeof(TResponse))
                {
                    throw new InvalidOperationException($"Register different type on same key. Key: {key} RegisteredType: ({subscription.ElementType.FullName},{subscription.ResponseType!.FullName}) NewType: ({typeof(TRequest).FullName}, {typeof(TResponse).FullName}");
                }

                handlerId = subscription.AddHandler(asyncHandler);
                return new Subscription(subscription, handlerId);
            }
            else
            {
                sid = Interlocked.Increment(ref _subscriptionId);

                subscription = new RefCountSubscription(this, sid, key, typeof(TRequest), typeof(TResponse));
                handlerId = subscription.AddHandler(asyncHandler);
                _bySubscriptionId[sid] = subscription;
                _byStringKey[key] = subscription;
            }
        }

        var returnSubscription = new Subscription(subscription, handlerId);
        try
        {
            await Connection.SubscribeAsync(sid, key, null).ConfigureAwait(false);
        }
        catch
        {
            returnSubscription.Dispose(); // can't subscribed, remove from holder.
            throw;
        }

        return returnSubscription;
    }

    public void PublishToClientHandlers(int subscriptionId, in ReadOnlySequence<byte> buffer)
    {
        RefCountSubscription? subscription;
        object?[] list;
        lock (Gate)
        {
            if (_bySubscriptionId.TryGetValue(subscriptionId, out subscription))
            {
                list = subscription.Handlers.GetValues();
            }
            else
            {
                return;
            }
        }

        MessagePublisher.Publish(subscription.ElementType, Connection.Options, buffer, list);
    }

    public void PublishToRequestHandler(int subscriptionId, in NatsKey replyTo, in ReadOnlySequence<byte> buffer)
    {
        RefCountSubscription? subscription;
        object?[] list;
        lock (Gate)
        {
            if (_bySubscriptionId.TryGetValue(subscriptionId, out subscription))
            {
                if (!subscription.IsRequestHandler)
                {
                    throw new InvalidOperationException($"Registered handler is not request handler.");
                }

                list = subscription.Handlers.GetValues();
            }
            else
            {
                return;
            }
        }

        foreach (var item in list)
        {
            if (item != null)
            {
                RequestPublisher.PublishRequest(subscription.ElementType, subscription.ResponseType!, Connection, replyTo, buffer, item);
                return;
            }
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            // remove all references.
            foreach (var item in _bySubscriptionId)
            {
                item.Value.Handlers.Dispose();
            }

            _bySubscriptionId.Clear();
            _byStringKey.Clear();
        }
    }

    internal void Remove(string key, int subscriptionId)
    {
        // inside lock from RefCountSubscription.RemoveHandler
        _byStringKey.Remove(key, out _);
        _bySubscriptionId.Remove(subscriptionId, out _);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly RefCountSubscription _rootSubscription;
        private readonly int _handlerId;
        private bool _isDisposed;

        public Subscription(RefCountSubscription rootSubscription, int handlerId)
        {
            _rootSubscription = rootSubscription;
            _handlerId = handlerId;
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _rootSubscription.RemoveHandler(_handlerId);
            }
        }
    }
}

internal sealed class RefCountSubscription
{
    private readonly SubscriptionManager _manager;

    public RefCountSubscription(SubscriptionManager manager, int subscriptionId, string key, Type elementType)
    {
        _manager = manager;
        SubscriptionId = subscriptionId;
        Key = key;
        ReferenceCount = 0;
        ElementType = elementType;
        Handlers = new FreeList<object>();
    }

    public RefCountSubscription(SubscriptionManager manager, int subscriptionId, string key, Type requestType, Type responseType)
    {
        _manager = manager;
        SubscriptionId = subscriptionId;
        Key = key;
        ReferenceCount = 0;
        ElementType = requestType;
        ResponseType = responseType;
        Handlers = new FreeList<object>();
    }

    public int SubscriptionId { get; }

    public string Key { get; }

    public NatsKey? QueueGroup { get; init; }

    public int ReferenceCount { get; private set; }

    public Type ElementType { get; } // or RequestType

    public Type? ResponseType { get; }

    public FreeList<object> Handlers { get; }

    public bool IsRequestHandler => ResponseType != null;

    // Add is in lock(gate)
    public int AddHandler(object handler)
    {
        var id = Handlers.Add(handler);
        ReferenceCount++;
        Interlocked.Increment(ref _manager.Connection.Counter.SubscriptionCount);
        return id;
    }

    public void RemoveHandler(int handlerId)
    {
        lock (_manager.Gate)
        {
            ReferenceCount--;
            Handlers.Remove(handlerId, false);
            Interlocked.Decrement(ref _manager.Connection.Counter.SubscriptionCount);
            if (ReferenceCount == 0)
            {
                _manager.Remove(Key, SubscriptionId);
                Handlers.Dispose();
                _manager.Connection.PostUnsubscribe(SubscriptionId);
            }
        }
    }
}
