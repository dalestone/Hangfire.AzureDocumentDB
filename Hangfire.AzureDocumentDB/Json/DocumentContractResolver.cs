﻿using System;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

using Hangfire.AzureDocumentDB.Entities;

namespace Hangfire.AzureDocumentDB.Json
{
    internal class DocumentContractResolver : DefaultContractResolver
    {
        protected override JsonContract CreateContract(Type objectType)
        {
            JsonContract contract = base.CreateContract(objectType);

            // assign the document contract if type is of DocumentEntity
            if (objectType == typeof(DocumentEntity))
            {
                contract.Converter = new DocumentConverter();
            }

            return contract;
        }
    }


    internal class DocumentConverter : JsonConverter
    {
        public override bool CanWrite => false;
        public override bool CanRead => true;
        public override bool CanConvert(Type objectType) => objectType == typeof(DocumentEntity);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new InvalidOperationException("Use default serialization.");
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jsonObject = JObject.Load(reader);
            DocumentEntity document;

            switch (jsonObject["type"].Value<int>())
            {
                case (int)DocumentTypes.Server:
                    document = new Entities.Server();
                    break;
                case (int)DocumentTypes.Job:
                    document = new Job();
                    break;
                case (int)DocumentTypes.Queue:
                    document = new Entities.Queue();
                    break;
                case (int)DocumentTypes.Counter:
                    document = new Counter();
                    break;
                case (int)DocumentTypes.List:
                    document = new List();
                    break;
                case (int)DocumentTypes.Hash:
                    document = new Hash();
                    break;
                case (int)DocumentTypes.Set:
                    document = new Set();
                    break;
                case (int)DocumentTypes.State:
                    document = new State();
                    break;
                case (int)DocumentTypes.Lock:
                    document = new Lock();
                    break;
                default: throw new ArgumentOutOfRangeException();
            }

            serializer.Populate(jsonObject.CreateReader(), document);
            return document;
        }
    }
}
