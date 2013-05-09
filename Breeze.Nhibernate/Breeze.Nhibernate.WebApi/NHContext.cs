﻿using Breeze.WebApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NHibernate;
using NHibernate.Cfg;
using NHibernate.Linq;
using NHibernate.Metadata;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;

namespace Breeze.Nhibernate.WebApi
{
    public class NHContext : ContextProvider, IDisposable
    {
        private ISession session;
        protected Configuration configuration;

        /// <summary>
        /// Create a new context for the given session.  
        /// Each thread should have its own NHContext and Session.
        /// </summary>
        /// <param name="session">Used for queries and updates</param>
        /// <param name="configuration">Used for metadata generation</param>
        public NHContext(ISession session, Configuration configuration)
        {
            this.session = session;
            this.configuration = configuration;
        }

        public ISession Session
        {
            get { return session; }
        }

        public NhQueryableInclude<T> GetQuery<T>()
        {
            return new NhQueryableInclude<T>(session.GetSessionImplementation());
        }

        public void Close()
        {
            if (session != null && session.IsOpen) session.Close();
        }

        public void Dispose()
        {
            Close();
        }

        #region Metadata

        protected override string BuildJsonMetadata()
        {
            var builder = new NHBreezeMetadata(session.SessionFactory, configuration);
            var meta = builder.BuildMetadata();

            var serializerSettings = new JsonSerializerSettings()
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };

            var json = JsonConvert.SerializeObject(meta, Formatting.Indented);
            return json;
        }

        #endregion
        #region Save Changes

        private Dictionary<EntityInfo, KeyMapping> EntityKeyMapping = new Dictionary<EntityInfo, KeyMapping>();
        private Dictionary<EntityInfo, ICollection<ValidationResult>> ValidationResults = new Dictionary<EntityInfo, ICollection<ValidationResult>>();

        /// <summary>
        /// Persist the changes to the entities in the saveMap.
        /// This implements the abstract method in ContextProvider
        /// </summary>
        /// <param name="saveMap">Map of Type -> List of entities of that type</param>
        /// <returns>List of KeyMappings, which map the temporary keys to their real generated keys</returns>
        protected override List<KeyMapping> SaveChangesCore(Dictionary<Type, List<EntityInfo>> saveMap)
        {
            using (var tx = session.BeginTransaction())
            {
                try
                {
                    ProcessSaves(saveMap);

                    if (ValidationResults.Any())
                    {
                        var msg = CollectValidationErrors();
                        throw new ValidationException(msg);
                    }

                    tx.Commit();
                    session.Flush();
                }
                catch (PropertyValueException pve)
                {
                    // NHibernate can throw this
                    tx.Rollback();
                    var msg = string.Format("'{0}' validation error: property={1}, message={2}", pve.EntityName, pve.PropertyName, pve.Message);
                    throw new ValidationException(msg);
                }
                catch (Exception)
                {
                    tx.Rollback();
                    throw;
                }
            }

            return UpdateAutoGeneratedKeys();
        }

        /// <summary>
        /// Concatenate all the validation messages together.
        /// </summary>
        /// <returns></returns>
        protected string CollectValidationErrors()
        {
            var sb = new StringBuilder();
            foreach (var kvp in ValidationResults)
            {
                var entityInfo = kvp.Key;
                var entity = entityInfo.Entity;
                var type = entity.GetType();
                var id = GetIdentifier(entity);

                foreach(var r in kvp.Value)
                {
                    sb.AppendFormat("\n'{0}';{1} validation error: {2}",
                        type.Name, id, r.ToString());
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Persist the changes to the entities in the saveMap.
        /// </summary>
        /// <param name="saveMap"></param>
        private void ProcessSaves(Dictionary<Type, List<EntityInfo>> saveMap)
        {
            foreach (var kvp in saveMap)
            {
                var entityType = kvp.Key;
                var classMeta = session.SessionFactory.GetClassMetadata(entityType);

                foreach (var entityInfo in kvp.Value)
                {
                    AddKeyMapping(entityInfo, entityType, classMeta);
                    ProcessEntity(entityInfo);
                }
            }
        }

        /// <summary>
        /// Add, update, or delete the entity according to its EntityState.
        /// </summary>
        /// <param name="entityInfo"></param>
        private void ProcessEntity(EntityInfo entityInfo)
        {
            var entity = entityInfo.Entity;
            var state = entityInfo.EntityState;

            // Perform validation on the entity, based on DataAnnotations
            var validationResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(entity, new ValidationContext(entity), validationResults, true))
            {
                ValidationResults.Add(entityInfo, validationResults);
                return;
            }

            if (state == EntityState.Modified)
            {
                session.Update(entity);
            }
            else if (state == EntityState.Added)
            {
                session.Save(entity);
            }
            else if (state == EntityState.Deleted)
            {
                session.Delete(entity);
            }
            else
            {
                // Just re-associate the entity with the session.  Needed for many to many to get both ends into the session.
                session.Lock(entity, LockMode.None);
            }
        }

        /// <summary>
        /// Record the value of the temporary key in EntityKeyMapping
        /// </summary>
        /// <param name="entityInfo"></param>
        private void AddKeyMapping(EntityInfo entityInfo, Type type, IClassMetadata meta)
        {
            var entity = entityInfo.Entity;
            var id = GetIdentifier(entity, meta);
            var km = new KeyMapping() { EntityTypeName = type.FullName, TempValue = id };
            EntityKeyMapping.Add(entityInfo, km);
        }

        /// <summary>
        /// Get the identifier value for the entity.  If the entity does not have an
        /// identifier property, or natural identifiers defined, then the entity itself is returned.
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="meta"></param>
        /// <returns></returns>
        private object GetIdentifier(object entity, IClassMetadata meta = null)
        {
            var type = entity.GetType();
            meta = meta ?? session.SessionFactory.GetClassMetadata(type);
            if (meta.HasIdentifierProperty)
            {
                return meta.GetIdentifier(entity, EntityMode.Poco);
            }
            else if (meta.HasNaturalIdentifier)
            {
                var idprops = meta.NaturalIdentifierProperties;
                var values = meta.GetPropertyValues(entity, EntityMode.Poco);
                var idvalues = idprops.Select(i => values[i]).ToArray();
                return idvalues;
            }
            return entity;
        }

        /// <summary>
        /// Update the KeyMappings with their real values.
        /// </summary>
        /// <returns></returns>
        private List<KeyMapping> UpdateAutoGeneratedKeys()
        {
            var list = new List<KeyMapping>();
            foreach (var entityInfo in EntitiesWithAutoGeneratedKeys)
            {
                KeyMapping km;
                if (EntityKeyMapping.TryGetValue(entityInfo, out km))
                {
                    if (km.TempValue != null)
                    {
                        var entity = entityInfo.Entity;
                        var id = GetIdentifier(entity);
                        km.RealValue = id;
                        list.Add(km);
                    }
                }
            }
            return list;
        }

        #endregion
    }
}