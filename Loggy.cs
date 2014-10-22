using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Timers;

namespace Loggy
{
    /// <summary>
    /// Loggy is a centralized, application level, runtime logging construct.  Give it your log messages and don't worry about them until you need them,
    /// if ever.  What if you don't need them?  No worries, Loggy only persists in memory, so when the AppDomain is unloaded, so is Loggy's persistence.
    /// Memory usage?  Loggy is lightweight and automatically trims it's entries in the background at predefined escalating limits to prevent bloat.  Of
    /// course, these limits are configurable and due diligence is required...if you push millions of entries into Loggy your available application
    /// resources must be capable.
    ///
    /// LICENSE
    /// The MIT License (MIT)
    /// Copyright(c) 2014 Paul Marvin
    /// http://opensource.org/licenses/MIT
    ///
    /// CONFIGURATION - AppSettings, completely optional
    /// LoggyPurgeTimerIntervalSeconds - default is 10 - determines the delay between purge timer executions
    /// LoggyPurgeTimerMaxEntries - default is 10000 - determines the max entry count before purge executions begin
    /// LoggyPurgeTimerPurgePercent - default is 30 - determines the percentage of entries to purge, oldest first, when max has been reached
    ///
    /// LOG
    /// Log a message with just a string parameter.
    /// Log a message with just an exception parameter.
    /// Each Log method supports passing a caller, but if you don't one is automatically assigned using the Stack.
    ///
    /// DUMP
    /// Dump - dumps each message into an IEnumerable<LoggerEntry> so you can have full access to the data.
    /// DumpAsString - dumps each message into an IEnumerable<string> so you can write it them to a log file.
    /// DumpAsTuple - dumps each message into an IEnumerable<Tuple> so you can interact with them without referencing the underlying LoggerEntry class.
    /// Each Dump supports passing the caller (typically the full type name of the method that called Log) which will filter the results accordingly.
    ///
    /// MISC
    /// Clear - empties the entire collection, no more entries.
    /// GetCaller - returns the expected caller string for the method that calls this one, useful for filtering
    /// ParseException - eh, it really just calls ToString, nothing fancy here
    /// Purge - empties the top N entries, oldest first
    /// </summary>
    public static class Loggy
    {
        private static readonly ConcurrentQueue<LoggyEntry> _entries;
        private static readonly Timer _purgeTimer;
        private static readonly int _purgeTimerDefaultIntervalSeconds;
        private static readonly int _purgeTimerDefaultMaxEntries;
        private static readonly int _purgeTimerDefaultPurgePercentage;
        private static readonly object _purgeTimerElapsedLock = new object();
        private static readonly object _purgeTimerInitLock = new object();
        private static volatile bool _purgeTimerInProcess = false;

        /// <summary>
        /// Initializes the <see cref="Loggy"/> class.
        /// </summary>
        static Loggy()
        {
            // Initialize backing collection
            _entries = new ConcurrentQueue<LoggyEntry>();

            // Initialize timer
            _purgeTimer = new Timer();
            GC.KeepAlive(_purgeTimer);

            // Initialize configuration
            _purgeTimerDefaultIntervalSeconds = GetApplicationSetting<int?>("LoggyPurgeTimerIntervalSeconds") ?? 10;
            _purgeTimerDefaultMaxEntries = GetApplicationSetting<int?>("LoggyPurgeTimerMaxEntries") ?? 10000;
            _purgeTimerDefaultPurgePercentage = GetApplicationSetting<int?>("LoggyPurgeTimerPurgePercent") ?? 30;
        }

        #region Public Actions

        /// <summary>
        /// Clears all entries.
        /// </summary>
        public static void Clear()
        {
            ClearImpl();
        }

        /// <summary>
        /// Dumps entries.
        /// </summary>
        /// <param name="caller">The caller.</param>
        /// <returns></returns>
        public static IEnumerable<LoggyEntry> Dump(string caller = null)
        {
            return DumpImpl(caller);
        }

        /// <summary>
        /// Dumps entries as string.
        /// </summary>
        /// <param name="caller">The caller.</param>
        /// <returns></returns>
        public static IEnumerable<string> DumpAsString(string caller = null)
        {
            return DumpAsStringImpl(caller);
        }

        /// <summary>
        /// Dumps entries as tuple.
        /// </summary>
        /// <param name="caller">The caller.</param>
        /// <returns></returns>
        public static IEnumerable<Tuple<DateTime, string, string>> DumpAsTuple(string caller = null)
        {
            return DumpAsTupleImpl(caller);
        }

        /// <summary>
        /// Logs the specified message.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="caller">The caller.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">message;message cannot be null</exception>
        public static Guid Log(string message, string caller = null)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new LoggyArgumentNullException("message", "message cannot be null");

            if (string.IsNullOrWhiteSpace(caller))
            {
                // Required for .NET 4.0 support, CallerMemberNameAttribute is available in .NET 4.5
                StackFrame frame = new StackFrame(1);
                var method = frame.GetMethod();
                var type = method.DeclaringType.FullName;
                var name = method.Name;

                caller = string.Format("{0}.{1}", type, name);
            }

            return LogImpl(message, caller);
        }

        /// <summary>
        /// Logs the specified exception.
        /// </summary>
        /// <param name="exception">The exception.</param>
        /// <param name="caller">The caller.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">exception;exception cannot be null</exception>
        public static Guid Log(Exception exception, string caller = null)
        {
            if (exception == null) throw new LoggyArgumentNullException("exception", "exception cannot be null");

            if (string.IsNullOrWhiteSpace(caller))
            {
                // Required for .NET 4.0 support, CallerMemberNameAttribute is available in .NET 4.5
                StackFrame frame = new StackFrame(1);
                var method = frame.GetMethod();
                var type = method.DeclaringType.FullName;
                var name = method.Name;

                caller = string.Format("{0}.{1}", type, name);
            }

            return LogImpl(exception, caller);
        }

        /// <summary>
        /// Purges the specified quantity of entries.
        /// </summary>
        /// <param name="quantityToPurge">The quantity to purge.</param>
        /// <returns></returns>
        public static int Purge(int quantityToPurge)
        {
            return PurgeImpl(quantityToPurge);
        }

        #endregion

        #region Private Actions

        private static void ClearImpl()
        {
            PurgeImpl(_entries.Count);
        }

        private static IEnumerable<string> DumpAsStringImpl(string caller = null)
        {
            if (!string.IsNullOrWhiteSpace(caller))
            {
                return _entries
                    .Where(c => c.Caller.Equals(caller, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.ToString())
                    .AsEnumerable();
            }

            return _entries
                .Select(c => c.ToString())
                .AsEnumerable();
        }

        private static IEnumerable<Tuple<DateTime, string, string>> DumpAsTupleImpl(string caller = null)
        {
            if (!string.IsNullOrWhiteSpace(caller))
            {
                return _entries
                    .Where(c => c.Caller.Equals(caller, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new Tuple<DateTime, string, string>(new DateTime(c.DateTicks), c.Caller, c.Message))
                    .AsEnumerable();
            }

            return _entries
                .Select(c => new Tuple<DateTime, string, string>(new DateTime(c.DateTicks), c.Caller, c.Message))
                .AsEnumerable();
        }

        private static IEnumerable<LoggyEntry> DumpImpl(string caller = null)
        {
            if (!string.IsNullOrWhiteSpace(caller))
            {
                return _entries
                    .Where(c => c.Caller.Equals(caller, StringComparison.OrdinalIgnoreCase))
                    .AsEnumerable();
            }

            return _entries.AsEnumerable();
        }

        private static Guid LogImpl(string message, string caller = null)
        {
            PurgeTimer_Initialize();

            var entry = new LoggyEntry(caller, message);

            Queue(entry);

            return entry.Id;
        }

        private static Guid LogImpl(Exception exception, string caller = null)
        {
            PurgeTimer_Initialize();

            var entry = new LoggyEntry(caller, exception);

            Queue(entry);

            return entry.Id;
        }

        private static int PurgeImpl(int quantityToPurge)
        {
            if (quantityToPurge < 0) throw new LoggyArgumentOutOfRangeException();
            if (quantityToPurge == 0) return 0;

            var count = 0;

            for (int i = 0; i < quantityToPurge; i++)
            {
                Dequeue();
                count++;
            }

            return count;
        }

        private static IEnumerable<LoggyEntry> RetrieveByCallerImpl(string caller)
        {
            return _entries.Where(c => c.Caller.Equals(caller, StringComparison.OrdinalIgnoreCase)).AsEnumerable();
        }

        private static LoggyEntry RetrieveByIdImpl(Guid id)
        {
            return _entries.Where(c => c.Id == id).SingleOrDefault();
        }

        #endregion

        #region Public Helpers

        /// <summary>
        /// Gets the caller string of the method that calls this one.
        /// Useful for automatically generating the caller string for filtering retrieval.
        /// </summary>
        /// <returns></returns>
        public static string GetCaller()
        {
            // Required for .NET 4.0 support, CallerMemberNameAttribute is available in .NET 4.5
            StackFrame frame = new StackFrame(1);
            var method = frame.GetMethod();
            var type = method.DeclaringType.FullName;
            var name = method.Name;

            return string.Format("{0}.{1}", type, name);
        }

        /// <summary>
        /// Parses the exception.
        /// </summary>
        /// <param name="exception">The exception.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">exception;exception cannot be null</exception>
        public static string ParseException(this Exception exception)
        {
            if (exception == null) throw new LoggyArgumentNullException("exception", "exception cannot be null");

            return exception.ToString();
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Dequeues.
        /// </summary>
        /// <returns></returns>
        private static LoggyEntry Dequeue()
        {
            LoggyEntry entry;
            _entries.TryDequeue(out entry);
            return entry;
        }

        /// <summary>
        /// Gets the application setting from App/Web.config and casts it to the requested type.  Note, if the cast fails you will receive
        /// the default value and no exception will be thrown.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="name">The name.</param>
        /// <returns></returns>
        private static T GetApplicationSetting<T>(string name)
        {
            T r = default(T);

            var config = ConfigurationManager.AppSettings[name];
            if (!string.IsNullOrWhiteSpace(config)) r = TryCast<T>(config);

            return r;
        }

        /// <summary>
        /// Handles the Elapsed event of the PurgeTimer.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ElapsedEventArgs"/> instance containing the event data.</param>
        private static void PurgeTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            lock (_purgeTimerElapsedLock) // prevent multi-threaded access
            {
                if (_purgeTimerInProcess) return; // skip if already running on another thread
                if (_entries.IsEmpty) return; // skip if no entries

                _purgeTimerInProcess = true; // set purge in-process on to prevent running on another thread

                var count = _entries.Count; // get total count
                if (count >= _purgeTimerDefaultMaxEntries) // if total count exceeds registered max
                {
                    // get count using percentage of max
                    var purgeCount = (int)Math.Round(count * (_purgeTimerDefaultPurgePercentage * .01M), 0, MidpointRounding.AwayFromZero);
                    if (purgeCount > 0) Purge(purgeCount); // purge specified count
                }

                _purgeTimerInProcess = false; // set purge in-process off
            }
        }

        /// <summary>
        /// Initializes the PurgeTimer.
        /// </summary>
        /// <exception cref="System.ApplicationException"></exception>
        private static void PurgeTimer_Initialize()
        {
            lock (_purgeTimerInitLock) // prevent multi-thread access
            {
                if (_purgeTimer == null) throw new LoggyException("Purge Timer was null"); // null shouldn't happen
                if (_purgeTimer.Enabled) return; // skip if already initialized

                _purgeTimer.Interval = _purgeTimerDefaultIntervalSeconds * 1000;
                _purgeTimer.Elapsed += PurgeTimer_Elapsed;
                _purgeTimer.Enabled = true;
            }
        }

        /// <summary>
        /// Queues the specified entry.
        /// </summary>
        /// <param name="entry">The entry.</param>
        private static void Queue(LoggyEntry entry)
        {
            _entries.Enqueue(entry);
        }

        /// <summary>
        /// Generic TryCast with support for nullable types and Guids.
        ///
        /// Credit to Eric Burcham
        /// http://stackoverflow.com/a/10839349/578859
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value">The value.</param>
        /// <returns></returns>
        private static T TryCast<T>(this object value)
        {
            var type = typeof(T);

            // If the type is nullable and the result should be null, set a null value.
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>) && (value == null || value == DBNull.Value))
            {
                return default(T);
            }

            // Convert.ChangeType fails on Nullable<T> types.  We want to try to cast to the underlying type anyway.
            var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

            try
            {
                // Just one edge case you might want to handle.
                if (underlyingType == typeof(Guid))
                {
                    if (value is string)
                    {
                        value = new Guid(value as string);
                    }

                    if (value is byte[])
                    {
                        value = new Guid(value as byte[]);
                    }

                    return (T)Convert.ChangeType(value, underlyingType);
                }

                return (T)Convert.ChangeType(value, underlyingType);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                if (Debugger.IsAttached) Debugger.Break();
                return default(T);
            }
        }

        #endregion

        /// <summary>
        /// Loggy's ArgumentNull Exception Class, in case you want to catch them explicitly, it just inherits from System.ArgumentNullException
        /// </summary>
        public class LoggyArgumentNullException : ArgumentNullException
        {
            public LoggyArgumentNullException() : base()
            {
            }

            public LoggyArgumentNullException(string paramName) : base(paramName)
            {
            }

            public LoggyArgumentNullException(string paramName, string message) : base(paramName, message)
            {
            }

            public LoggyArgumentNullException(string message, Exception innerException) : base(message, innerException)
            {
            }
        }

        /// <summary>
        /// Loggy's ArgumentOutOfRange Exception Class, in case you want to catch them explicitly, it just inherits from System.ArgumentOutOfRangeException
        /// </summary>
        public class LoggyArgumentOutOfRangeException : ArgumentOutOfRangeException
        {
            public LoggyArgumentOutOfRangeException() : base()
            {
            }

            public LoggyArgumentOutOfRangeException(string paramName) : base(paramName)
            {
            }

            public LoggyArgumentOutOfRangeException(string paramName, string message) : base(paramName, message)
            {
            }

            public LoggyArgumentOutOfRangeException(string message, Exception innerException) : base(message, innerException)
            {
            }
        }

        /// <summary>
        /// Loggy's LoggyEntry Class or what's actually used for persistence in the backing collection
        /// </summary>
        public class LoggyEntry
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="LoggyEntry"/> class.
            /// </summary>
            /// <param name="caller">The caller.</param>
            /// <param name="message">The message.</param>
            public LoggyEntry(string caller, string message)
            {
                this.Caller = caller;
                this.DateTicks = DateTime.UtcNow.Ticks;
                this.Id = Guid.NewGuid();
                this.Message = message;
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="LoggyEntry"/> class.
            /// </summary>
            /// <param name="caller">The caller.</param>
            /// <param name="exception">The exception.</param>
            public LoggyEntry(string caller, Exception exception)
            {
                this.Caller = caller;
                this.DateTicks = DateTime.UtcNow.Ticks;
                this.Id = Guid.NewGuid();
                this.Message = exception.ParseException();
            }

            /// <summary>
            /// Gets or sets the caller.
            /// </summary>
            /// <value>
            /// The caller.
            /// </value>
            public string Caller { get; set; }

            /// <summary>
            /// Gets or sets the date in ticks.
            /// </summary>
            /// <value>
            /// The date.
            /// </value>
            public long DateTicks { get; set; }

            /// <summary>
            /// Gets or sets the unique identifier.
            /// </summary>
            /// <value>
            /// The unique identifier.
            /// </value>
            public Guid Id { get; set; }

            /// <summary>
            /// Gets or sets the message.
            /// </summary>
            /// <value>
            /// The message.
            /// </value>
            public string Message { get; set; }

            /// <summary>
            /// Returns a hash code for this instance.
            /// </summary>
            /// <returns>
            /// A hash code for this instance, suitable for use in hashing algorithms and data structures like a hash table.
            /// </returns>
            public override int GetHashCode()
            {
                // https://stackoverflow.com/questions/263400/what-is-the-best-algorithm-for-an-overridden-system-object-gethashcode/263416#263416
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + (!string.IsNullOrWhiteSpace(this.Caller) ? this.Caller.GetHashCode() : 0);
                    hash = hash * 31 + this.DateTicks.GetHashCode();
                    hash = hash * 31 + this.Id.GetHashCode();
                    hash = hash * 31 + (!string.IsNullOrWhiteSpace(this.Message) ? this.Message.GetHashCode() : 0);
                    return hash;
                }
            }

            /// <summary>
            /// Returns a <see cref="System.String" /> that represents this instance.
            /// </summary>
            /// <returns>
            /// A <see cref="System.String" /> that represents this instance.
            /// </returns>
            public override string ToString()
            {
                return string.Format("{0} -- {1} -- {2}", new DateTime(this.DateTicks).ToString("YYYY/MM/dd HH:mm:fffffff"), this.Caller, this.Message);
            }
        }

        /// <summary>
        /// Loggy's Generic Exception Class, in case you want to catch them explicitly, it just inherits from System.Exception
        /// </summary>
        public class LoggyException : Exception
        {
            public LoggyException() : base()
            {
            }

            public LoggyException(string message) : base(message)
            {
            }

            public LoggyException(string message, Exception innerException) : base(message, innerException)
            {
            }
        }
    }
}