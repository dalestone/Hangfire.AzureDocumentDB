﻿using System;
using Hangfire.AzureDocumentDB;

// ReSharper disable UnusedMember.Global
namespace Hangfire
{
    /// <summary>
    /// Extension methods to user AzureDocumentDb Storage.
    /// </summary>
    public static class AzureDocumentDbStorageExtensions
    {
        /// <summary>
        /// Enables to attache AzureDocumentDb to Hangfire
        /// </summary>
        /// <param name="configuration">The IGlobalConfiguration object</param>
        /// <param name="url">The url string to AzureDocumentDb Database</param>
        /// <param name="authSecret">The secret key for the AzureDocumentDb Database</param>
        /// <param name="database">The name of the database to connect with</param>
        /// <param name="collection">The name of the collection on the database</param>
        /// <returns></returns>
        public static IGlobalConfiguration<AzureDocumentDbStorage> UseAzureDocumentDbStorage(this IGlobalConfiguration configuration, string url, string authSecret, string database, string collection)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (string.IsNullOrEmpty(url)) throw new ArgumentNullException(nameof(url));
            if (string.IsNullOrEmpty(authSecret)) throw new ArgumentNullException(nameof(authSecret));

            AzureDocumentDbStorage storage = new AzureDocumentDbStorage(url, authSecret, database, collection);
            return configuration.UseStorage(storage);
        }

        /// <summary>
        /// Enables to attache AzureDocumentDb to Hangfire
        /// </summary>
        /// <param name="configuration">The IGlobalConfiguration object</param>
        /// <param name="url">The url string to AzureDocumentDb Database</param>
        /// <param name="authSecret">The secret key for the AzureDocumentDb Database</param>
        /// <param name="database">The name of the database to connect with</param>
        /// <param name="collection">The name of the collection on the database</param>
        /// <param name="options">The AzureDocumentDbStorage object to override any of the options</param>
        /// <returns></returns>
        public static IGlobalConfiguration<AzureDocumentDbStorage> UseAzureDocumentDbStorage(this IGlobalConfiguration configuration, string url, string authSecret, string database, string collection, AzureDocumentDbStorageOptions options)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (string.IsNullOrEmpty(url)) throw new ArgumentNullException(nameof(url));
            if (string.IsNullOrEmpty(authSecret)) throw new ArgumentNullException(nameof(authSecret));
            if (options == null) throw new ArgumentNullException(nameof(options));

            AzureDocumentDbStorage storage = new AzureDocumentDbStorage(url, authSecret, database, collection, options);
            return configuration.UseStorage(storage);
        }
    }
}
