using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace WPF
{
    internal static class GetMemberFuncCache<TFrom, TReturn>
    {
        private static readonly ConcurrentDictionary<(Type fromType, string memberName), Func<TFrom, TReturn>> Cache = 
            new ConcurrentDictionary<(Type fromType, string memberName), Func<TFrom, TReturn>>();

        internal static Func<TFrom, TReturn> GetCache(MemberInfo memberInfo)
        {
            return Cache.GetOrAdd((memberInfo.DeclaringType, memberInfo.Name), _ =>
            {
                var instance = Expression.Parameter(typeof(TFrom), "instance");

                var castInstance = Expression.Convert(instance, memberInfo.DeclaringType);

                Expression body;

                switch (memberInfo)
                {
                    case PropertyInfo propertyInfo:
                        body = Expression.Call(castInstance, propertyInfo.GetMethod);
                        break;
                    case FieldInfo fieldInfo:
                        body = Expression.Field(castInstance, fieldInfo);
                        break;
                    default:
                        throw new ArgumentException($"Cannot handle member {memberInfo.MemberType}", nameof(memberInfo));
                }

                var parameters = new[] { instance };

                var lambdaExpression = Expression.Lambda<Func<TFrom, TReturn>>(body, parameters);

                return lambdaExpression.Compile();
            });
        }
    }

    internal static class ExpressionExtensions
    {
        internal static object GetParentForExpression(this HashSet<Func<object, object>> chain, object startItem)
        {
            var current = startItem;

            foreach(var valueFetcher in chain)
            {
                current = valueFetcher.Invoke(current);
            }

            return current;
        }

        internal static HashSet<Func<object, object>> GetValueMemberChain(this Expression expression)
        {
            var returnValue = new HashSet<Func<object, object>>();

            var expressionChain = expression.GetExpressionChain();

            foreach (var value in expressionChain.Take(expressionChain.Count - 1))
            {
                var valueFetcher = GetMemberFuncCache<object, object>.GetCache(value.Member);

                returnValue.Add(valueFetcher);
            }   

            return returnValue;
        }

        internal static HashSet<MemberExpression> GetExpressionChain(this Expression expression)
        {
            var expressions = new HashSet<MemberExpression>();

            var node = expression;

            while (node.NodeType != ExpressionType.Parameter)
            {
                switch (node.NodeType)
                {
                    case ExpressionType.MemberAccess:
                        var memberExpression = (MemberExpression)node;
                        expressions.Add(memberExpression);
                        node = memberExpression.Expression;
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported expression type: '{node.NodeType}'");
                }
            }

            expressions.Reverse();

            return expressions;
        }

    }

    public static class NotifyPropertyChangedExtensions
    {
        public static IObservable<TReturn> WhenPropertyValueChanges<TObj, TReturn>(
            this TObj objectToMonitor,
            Expression<Func<TObj, TReturn>> propertyExpression) 
            where TObj : class, INotifyPropertyChanged
           
        {
            if (propertyExpression is null)
                throw new ArgumentNullException(nameof(propertyExpression));
        
            return WhenPropertyChanges(objectToMonitor, propertyExpression).Select(x => x.Value);
        }

        public static IObservable<(object Sender, TReturn Value)> WhenPropertyChanges<TObj, TReturn>(
            this TObj objectToMonitor, 
            Expression<Func<TObj, TReturn>> propertyExpression) 
            where TObj : class, INotifyPropertyChanged
        {
            if (propertyExpression is null)
                throw new ArgumentNullException(nameof(propertyExpression));

            IObservable<(object sender, INotifyPropertyChanged value)> currentObservable = Observable.Return((default(object), (INotifyPropertyChanged)objectToMonitor));

            var expressionChain = propertyExpression.Body
                .GetExpressionChain();

            if (expressionChain.Count == 0)
                throw new ArgumentException("There are no fields in the expression", nameof(propertyExpression));

            var i = 0;

            foreach (var memberExpression in expressionChain)
            {
                if (i == expressionChain.Count - 1)
                {
                    return currentObservable
                        .Where(parent => parent.value != null)
                        .Select(parent => GenerateObservable<TReturn>(parent.value, memberExpression))
                        .Switch();
                }

                currentObservable = currentObservable
                    .Where(parent => parent.value != null)
                    .Select(parent => GenerateObservable<INotifyPropertyChanged>(parent.value, memberExpression))
                    .Switch();

                i++;
            }
        
            throw new ArgumentException("Invalid expression", nameof(propertyExpression));
        }

        public static IObservable<bool> WhenAnyPropertyValuesChange<TObj, TPropertyType1, TPropertyType2>(
            this TObj objectToMonitor,
            Expression<Func<TObj, TPropertyType1>> propertyExpression1,
            Expression<Func<TObj, TPropertyType2>> propertyExpression2)
            where TObj : class, INotifyPropertyChanged
        {
            var firstObservable = objectToMonitor.WhenPropertyValueChanges(propertyExpression1);

            var secondObservable = objectToMonitor.WhenPropertyValueChanges(propertyExpression2);

            return firstObservable.CombineLatest(secondObservable, (o1, o2) => true);
        }

        internal static IObservable<(object Sender, TReturn Value)> GenerateObservable<TReturn>(
            INotifyPropertyChanged parent, 
            MemberExpression memberExpression)
        {
            var memberInfo = memberExpression.Member;
            var memberName = memberInfo.Name;

            var func = GetMemberFuncCache<INotifyPropertyChanged, TReturn>.GetCache(memberInfo);

            var observable = Observable.FromEvent<PropertyChangedEventHandler, (object Sender, PropertyChangedEventArgs EventArgs)>(
                handler =>
                {
                    void Handler(object s, PropertyChangedEventArgs e) => handler((s, e));
                    return Handler;
                },
                x => parent.PropertyChanged += x,
                x => parent.PropertyChanged -= x);
                
            return observable
                .Where(x => x.EventArgs.PropertyName == memberName)
                .Select(x => (x.Sender, func.Invoke(parent)))
                .StartWith((parent, func.Invoke(parent)));
        }

        public static IObservable<string> GeneratePropertyChangesObservable<TObj>(this TObj objectToMonitor)
             where TObj : class, INotifyPropertyChanged
        {
            var observable = Observable.FromEvent<PropertyChangedEventHandler, (object Sender, PropertyChangedEventArgs EventArgs)>(
                handler =>
                {
                    void Handler(object s, PropertyChangedEventArgs e) => handler((s, e));
                    return Handler;
                },
                x => objectToMonitor.PropertyChanged += x,
                x => objectToMonitor.PropertyChanged -= x);

            return observable
                .Select(x => x.EventArgs.PropertyName);
        }

        public static IDisposable InvokeWhenPropertyValueChanges<TObj, TProperty>(
            this TObj obj, 
            Expression<Func<TObj, TProperty>> propertyExpression, 
            Action action)
            where TObj : class, INotifyPropertyChanged
        {
            var observable = obj
                .WhenPropertyValueChanges(propertyExpression)
                .Select(property => property);

            return observable
                .ObserveOn(ImmediateScheduler.Instance)
                .Subscribe(_ => action.Invoke());
        }

        public static IDisposable InvokeWhenPropertyValueChanges<TObj, TProperty>(
            this TObj obj,
            Expression<Func<TObj, TProperty>> propertyExpression,
            Action<TObj> action)
            where TObj : class, INotifyPropertyChanged
        {
            var observable = obj
                .WhenPropertyValueChanges(propertyExpression)
                .Select(property => property);

            return observable
                .ObserveOn(ImmediateScheduler.Instance)
                .Subscribe(_ => action.Invoke(obj));
        }

        public static IDisposable InvokeWhenAnyPropertyValueChanges<TObj>(
            this TObj obj, 
            Action<string> action)
            where TObj : class, INotifyPropertyChanged
        {
            return obj
                .GeneratePropertyChangesObservable()
                .Select(property => property)
                .ObserveOn(ImmediateScheduler.Instance)
                .Subscribe(o => action.Invoke(o));
        }
    }

    public abstract class AbstractNotifyPropertyChanged : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null) => 
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected virtual void SetAndRaise<T>(
            ref T backingFiled, 
            T newValue, 
            [CallerMemberName] string propertyName = null) => 
            SetAndRaise(ref backingFiled, newValue, EqualityComparer<T>.Default, propertyName);

        protected virtual void SetAndRaise<T>(
            ref T backingFiled, 
            T newValue, 
            IEqualityComparer<T> comparer, 
            [CallerMemberName] string propertyName = null)
        {
            comparer = comparer ?? EqualityComparer<T>.Default;

            if (!comparer.Equals(backingFiled, newValue))
            {
                backingFiled = newValue;
                OnPropertyChanged(propertyName);
            }
        }
    }
}
