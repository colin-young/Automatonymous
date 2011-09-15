﻿// Copyright 2011 Chris Patterson, Dru Sellers
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use 
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed 
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace Automatonymous
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using Impl;
    using Impl.Activities;
    using Internal;
    using Internal.Caching;


    public interface StateMachine
    {
        IEnumerable<Event> Events { get; }
        IEnumerable<State> States { get; }
        Type InstanceType { get; }

        IEnumerable<Event> NextEvents(State state);
    }


    public abstract class StateMachine<TInstance> :
        StateMachineNode,
        StateMachine
        where TInstance : class, StateMachineInstance
    {
        readonly State<TInstance> _anyState;
        readonly Cache<string, Event> _eventCache;
        readonly Activity<TInstance> _initialActivity;
        readonly Cache<string, State<TInstance>> _stateCache;

        protected StateMachine()
        {
            _stateCache = new DictionaryCache<string, State<TInstance>>();
            _eventCache = new DictionaryCache<string, Event>();

            State(() => Initial);
            State(() => Completed);

            _initialActivity = new TransitionActivity<TInstance>(Initial);

            _anyState = new StateImpl<TInstance>(".Any");
        }

        public State Initial { get; private set; }
        public State Completed { get; private set; }

        public IEnumerable<State> States
        {
            get { return _stateCache; }
        }

        public IEnumerable<Event> Events
        {
            get { return _eventCache; }
        }

        public Type InstanceType
        {
            get { return typeof(TInstance); }
        }

        public IEnumerable<Event> NextEvents(State state)
        {
            return _stateCache[state.Name].Events
                .Concat(_anyState.Events)
                .Distinct(new NameEqualityComparer());
        }

        public void Inspect(StateMachineInspector inspector)
        {
            Initial.Inspect(inspector);

            _stateCache.Each(x =>
                {
                    if (x == Initial || x == Completed)
                        return;

                    x.Inspect(inspector);
                });

            _anyState.Inspect(inspector);

            Completed.Inspect(inspector);
        }

        public void RaiseEvent(TInstance instance, Event @event)
        {
            WithInstance(instance, x =>
                {
                    _stateCache[instance.CurrentState.Name].Raise(instance, @event, null);
                    _anyState.Raise(instance, @event, null);
                });
        }

        public void RaiseEvent<TData>(TInstance instance, Event<TData> @event, TData value)
            where TData : class
        {
            WithInstance(instance, x =>
                {
                    _stateCache[instance.CurrentState.Name].Raise(instance, @event, value);
                    _anyState.Raise(instance, @event, value);
                });
        }

        void WithInstance(TInstance instance, Action<TInstance> callback)
        {
            if (instance == null)
                throw new ArgumentNullException("instance");

            if (instance.CurrentState == null)
                _initialActivity.Execute(instance, null);

            callback(instance);
        }

        protected void Event(Expression<Func<Event>> propertyExpression)
        {
            PropertyInfo property = propertyExpression.GetPropertyInfo();

            string name = property.Name;

            var @event = new SimpleEvent(name);

            property.SetValue(this, @event, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                null, null);

            _eventCache[name] = @event;
        }

        protected void Event(Expression<Func<Event>> propertyExpression,
                             Expression<Func<TInstance, int>> trackingPropertyExpression, params Event[] events)
        {
            if (events.Length > 31)
                throw new ArgumentException("No more than 31 events can be combined into a single event");

            PropertyInfo eventProperty = propertyExpression.GetPropertyInfo();
            PropertyInfo trackingPropertyInfo = trackingPropertyExpression.GetPropertyInfo();

            var trackingProperty = new FastProperty<TInstance, int>(trackingPropertyInfo);

            string name = eventProperty.Name;

            var @event = new SimpleEvent(name);

            eventProperty.SetValue(this, @event, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                null, null);

            _eventCache[name] = @event;

            int complete = Enumerable.Range(0, events.Length).Aggregate(0, (current, x) => current | (1 << x));

            for (int i = 0; i < events.Length; i++)
            {
                int flag = 1 << i;

                During(_anyState,
                    When(events[i])
                        .Then(instance =>
                            {
                                int value = trackingProperty.Get(instance);
                                value |= flag;

                                trackingProperty.Set(instance, value);
                                if (value == complete)
                                    RaiseEvent(instance, @event);
                            }));
            }
        }

        protected void Event<T>(Expression<Func<Event<T>>> propertyExpression)
            where T : class
        {
            PropertyInfo property = propertyExpression.GetPropertyInfo();

            string name = property.Name;

            var @event = new DataEvent<T>(name);

            property.SetValue(this, @event, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                null, null);

            _eventCache[name] = @event;
        }

        protected void State(Expression<Func<State>> propertyExpression)
        {
            PropertyInfo property = propertyExpression.GetPropertyInfo();

            string name = property.Name;

            var state = new StateImpl<TInstance>(name);

            property.SetValue(this, state, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                null, null);

            _stateCache[name] = state;
        }


        protected void During(State state, params IEnumerable<EventActivity<TInstance>>[] activities)
        {
            State<TInstance> activityState = state.For<TInstance>();

            foreach (var activity in activities.SelectMany(x => x))
                activityState.Bind(activity);
        }

        protected void Initially(params IEnumerable<EventActivity<TInstance>>[] activities)
        {
            During(_stateCache["Initial"], activities);
        }

        protected void Anytime(params IEnumerable<EventActivity<TInstance>>[] activities)
        {
            During(_anyState, activities);
        }

        protected EventActivityBinder<TInstance> When(Event @event)
        {
            return new SimpleEventActivityBinder<TInstance>(this, @event);
        }

        protected EventActivityBinder<TInstance, TData> When<TData>(Event<TData> @event)
            where TData : class
        {
            return new DataEventActivityBinder<TInstance, TData>(this, @event);
        }

        protected EventActivityBinder<TInstance, TData> When<TData>(Event<TData> @event,
                                                                    Expression<Func<TData, bool>> filterExpression)
            where TData : class
        {
            return new DataEventActivityBinder<TInstance, TData>(this, @event, filterExpression);
        }


        class NameEqualityComparer : IEqualityComparer<Event>
        {
            public bool Equals(Event x, Event y)
            {
                return Equals(x.Name, y.Name);
            }

            public int GetHashCode(Event @event)
            {
                return @event.Name.GetHashCode();
            }
        }
    }
}