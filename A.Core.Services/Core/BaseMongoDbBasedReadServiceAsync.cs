﻿using A.Core.Interface;
using A.Core.Model;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Configuration;

using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Linq.Dynamic;

namespace A.Core.Services.Core
{
    public partial class BaseMongoDbBasedReadServiceAsync<TEntity, TSearchObject, TSearchAdditionalData> : BaseService, IReadServiceAsync<TEntity, TSearchObject, TSearchAdditionalData>
        where TEntity : class, new()
        where TSearchAdditionalData : BaseAdditionalSearchRequestData, new()
        where TSearchObject : BaseSearchObject<TSearchAdditionalData>, new()
    {
        private static object syncLock = new Object();
        private static IMongoDatabase dataBase = null;

        public Lazy<IActionContext> ActionContext { get; set; }
        public virtual int DefaultPageSize { get; set; }
        public virtual string DatabaseName { get; set; }
        public virtual string ConnectionStringName { get; set; }
        public BaseMongoDbBasedReadServiceAsync()
        {
            // NOTE, this can be changed in T4 script
            DefaultPageSize = 100;
            DatabaseName = "MongoDb";
            ConnectionStringName = "MongoDb";
        }

        protected virtual IMongoDatabase GetDocumentDatabase()
        {
            if (dataBase == null)
            {
                lock (syncLock)
                {
                    var connectionString = GetConnectionStringForDocuments();
                    // Create a MongoClient object by using the connection string
                    var client = new MongoClient(connectionString);
                    // Use the server to access the 'test' database
                    dataBase = client.GetDatabase(DatabaseName);
                }
            }

            return dataBase;
        }
        protected virtual string GetConnectionStringForDocuments()
        {
            //add System.Configuration
            var connectionString = ConfigurationManager.ConnectionStrings[ConnectionStringName].ConnectionString;
            return connectionString;
        }
        protected virtual IMongoCollection<TEntity> GetCollection()
        {
            return GetDocumentDatabase().GetCollection<TEntity>(typeof(TEntity).Name);
        }
        protected virtual TEntity CreateNewInstance()
        {
            TEntity entity = new TEntity();
            return entity;
        }

        public virtual async Task<TEntity> GetAsync(object id, TSearchAdditionalData additionalData = null)
        {
            /*
             NOTE: This will work for entities that has following attribute on Id field
             [Key]
             [BsonId(IdGenerator = typeof(StringObjectIdGenerator))]
             */
            var objId = id.ToString();//ObjectId.Parse(id.ToString());
            var filter = Builders<TEntity>.Filter.Eq("_id", objId);
            var result = await GetCollection().FindAsync(filter);

            return await result.FirstOrDefaultAsync();
        }

        public virtual async Task<PagedResult<TEntity>> GetPageAsync(TSearchObject search)
        {
            if (search == null)
            {
                search = new TSearchObject(); //if we don't get search object, instantiate default
            }
            PagedResult<TEntity> result = new PagedResult<TEntity>();
            var query = await GetAsync(search);
            if (search.IncludeCount.GetValueOrDefault(false) == true)
            {
                result.Count = await GetCountAsync(query);
            }

            AddPaging(search, ref query);
            result.ResultList = await ((IMongoQueryable < TEntity >)query).ToListAsync();
            result.HasMore = result.ResultList.Count >= search.PageSize.GetValueOrDefault(DefaultPageSize) && result.ResultList.Count > 0;

            return result;
        }
        protected virtual async Task<long> GetCountAsync(IQueryable<TEntity> query)
        {
            return await ((IMongoQueryable<TEntity>)query).LongCountAsync();
        }
        public virtual async Task<IQueryable<TEntity>> GetAsync(TSearchObject search)
        {
            IQueryable<TEntity> query = GetCollection().AsQueryable<TEntity>();
            query = await AddFilterAsync(search, query);
            AddOrder(search, ref query);
            return await Task.FromResult(query);
        }

        protected virtual IList<string> GetIncludeList(TSearchAdditionalData searchAdditionalData)
        {
            var include = searchAdditionalData.IncludeList.ToArray();
            return include;
        }

        protected virtual Task<IQueryable<TEntity>> AddFilterAsync(TSearchObject search, IQueryable<TEntity> query)
        {
            AddFilterFromGeneratedCode(search, ref query);
            return Task.FromResult(query);
        }
        protected virtual void AddFilterFromGeneratedCode(TSearchObject search, ref IQueryable<TEntity> query)
        {

        }
        public virtual void AddPaging(TSearchObject search, ref IQueryable<TEntity> query)
        {
            if (!search.RetrieveAll.GetValueOrDefault(false) == true)
            {
                query = query.Skip(search.Page.GetValueOrDefault(0)
                    * search.PageSize.GetValueOrDefault(DefaultPageSize))
                    .Take(search.PageSize.GetValueOrDefault(DefaultPageSize));
            }
        }
        protected virtual void AddOrder(TSearchObject search, ref IQueryable<TEntity> query)
        {
            if (!string.IsNullOrWhiteSpace(search.OrderBy))
            {
                var items = search.OrderBy.Split(' ');
                if (items.Length > 2 || items.Length == 0)
                {
                    throw new ApplicationException("You can only sort by one field");
                }
                if (items.Length == 1)
                {
                    query = query.OrderBy("@0", search.OrderBy);
                }
                else
                {
                    query = query.OrderBy(string.Format("{0} {1}", items[0], items[1]));
                }
            }
            else
            {
                //If client didn't specify order, we wil add default one
                var propertyList =
                    typeof(TEntity).GetProperties().Where(x => x.GetCustomAttributes(typeof(KeyAttribute), true).Count() > 0);
                if (propertyList.Count() != 1)
                {
                    throw new System.Exception(
                        string.Format(
                            "KeyAttribute not found, or found multiple times on: {0}. Please override this implementation",
                            typeof(TEntity)));
                }

                search.OrderBy = propertyList.First().Name;
                query = query.OrderBy(search.OrderBy);
            }
        }
        public bool BeginTransaction()
        {
            return false;
        }

        public void CommitTransaction()
        {
           
        }

        public void RollbackTransaction()
        {
            
        }

        public void DisposeTransaction()
        {
           
        }

        public virtual A.Core.Validation.ValidationResult Validate(object entity)
        {
            A.Core.Validation.ValidationResult result = new A.Core.Validation.ValidationResult();

            var context = new ValidationContext(entity, serviceProvider: null, items: null);
            var validationResults = new List<ValidationResult>();

            bool isValid = Validator.TryValidateObject(entity, context, validationResults, true);
            if (!isValid)
            {
                validationResults.ForEach(x => { result.ResultList.Add(new A.Core.Validation.ValidationResultItem() { Key = x.MemberNames.FirstOrDefault(), Description = x.ErrorMessage }); });
            }
            return result;
        }
        public virtual async Task<bool> SaveAsync(TEntity entity)
        {
            if (entity is BaseEntityWithDateTokens)
            {
                var tmpEntity = entity as BaseEntityWithDateTokens;
                if (tmpEntity.CreatedOn == DateTime.MinValue)
                {
                    tmpEntity.CreatedOn = DateTime.UtcNow;
                }

                tmpEntity.ModifiedOn = DateTime.UtcNow;
            }

            if (entity is BaseEntityWithDateAndUserTokens)
            {
                object userIdObj;
                if (ActionContext.Value.Data.TryGetValue("UserId", out userIdObj))
                {
                    string userId = userIdObj.ToString();
                    var tmpEntity = entity as BaseEntityWithDateAndUserTokens;
                    if (string.IsNullOrWhiteSpace(tmpEntity.CreatedById))
                    {
                        tmpEntity.CreatedById = userId;
                    }

                    tmpEntity.ModifiedById = userId;
                }
            }

            var validationResult = Validate(entity);
            if (validationResult.HasErrors)
            {
                throw new A.Core.Validation.ValidationException(validationResult);
            }
           //upsert here
            await UpsertAsync(entity);

            return true;
        }

        protected virtual async Task UpsertAsync(TEntity entity)
        {
            throw new MissingMethodException("Upsert has to be implemented");
        }
    }
}
