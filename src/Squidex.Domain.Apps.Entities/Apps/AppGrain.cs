﻿// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschränkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Squidex.Domain.Apps.Core.Apps;
using Squidex.Domain.Apps.Entities.Apps.Commands;
using Squidex.Domain.Apps.Entities.Apps.Guards;
using Squidex.Domain.Apps.Entities.Apps.Services;
using Squidex.Domain.Apps.Entities.Apps.State;
using Squidex.Domain.Apps.Events;
using Squidex.Domain.Apps.Events.Apps;
using Squidex.Infrastructure;
using Squidex.Infrastructure.Commands;
using Squidex.Infrastructure.EventSourcing;
using Squidex.Infrastructure.Reflection;
using Squidex.Infrastructure.States;
using Squidex.Shared.Users;

namespace Squidex.Domain.Apps.Entities.Apps
{
    public class AppGrain : DomainObjectGrain<AppState>
    {
        private readonly InitialPatterns initialPatterns;
        private readonly IAppProvider appProvider;
        private readonly IAppPlansProvider appPlansProvider;
        private readonly IAppPlanBillingManager appPlansBillingManager;
        private readonly IUserResolver userResolver;

        public AppGrain(
            InitialPatterns initialPatterns,
            IStore<Guid> store,
            IAppProvider appProvider,
            IAppPlansProvider appPlansProvider,
            IAppPlanBillingManager appPlansBillingManager,
            IUserResolver userResolver)
            : base(store)
        {
            Guard.NotNull(initialPatterns, nameof(initialPatterns));
            Guard.NotNull(appProvider, nameof(appProvider));
            Guard.NotNull(userResolver, nameof(userResolver));
            Guard.NotNull(appPlansProvider, nameof(appPlansProvider));
            Guard.NotNull(appPlansBillingManager, nameof(appPlansBillingManager));

            this.userResolver = userResolver;
            this.appProvider = appProvider;
            this.appPlansProvider = appPlansProvider;
            this.appPlansBillingManager = appPlansBillingManager;
            this.initialPatterns = initialPatterns;
        }

        public override Task<object> ExecuteAsync(IAggregateCommand command)
        {
            switch (command)
            {
                case CreateApp createApp:
                    return CreateAsync(createApp, async c =>
                    {
                        await GuardApp.CanCreate(c, appProvider);

                        Create(c);
                    });

                case AssignContributor assigneContributor:
                    return UpdateAsync(assigneContributor, async c =>
                    {
                        await GuardAppContributors.CanAssign(Snapshot.Contributors, c, userResolver, appPlansProvider.GetPlan(Snapshot.Plan?.PlanId));

                        AssignContributor(c);
                    });

                case RemoveContributor removeContributor:
                    return UpdateAsync(removeContributor, c =>
                    {
                        GuardAppContributors.CanRemove(Snapshot.Contributors, c);

                        RemoveContributor(c);
                    });

                case AttachClient attachClient:
                    return UpdateAsync(attachClient, c =>
                    {
                        GuardAppClients.CanAttach(Snapshot.Clients, c);

                        AttachClient(c);
                    });

                case UpdateClient updateClient:
                    return UpdateAsync(updateClient, c =>
                    {
                        GuardAppClients.CanUpdate(Snapshot.Clients, c);

                        UpdateClient(c);
                    });

                case RevokeClient revokeClient:
                    return UpdateAsync(revokeClient, c =>
                    {
                        GuardAppClients.CanRevoke(Snapshot.Clients, c);

                        RevokeClient(c);
                    });

                case AddLanguage addLanguage:
                    return UpdateAsync(addLanguage, c =>
                    {
                        GuardAppLanguages.CanAdd(Snapshot.LanguagesConfig, c);

                        AddLanguage(c);
                    });

                case RemoveLanguage removeLanguage:
                    return UpdateAsync(removeLanguage, c =>
                    {
                        GuardAppLanguages.CanRemove(Snapshot.LanguagesConfig, c);

                        RemoveLanguage(c);
                    });

                case UpdateLanguage updateLanguage:
                    return UpdateAsync(updateLanguage, c =>
                    {
                        GuardAppLanguages.CanUpdate(Snapshot.LanguagesConfig, c);

                        UpdateLanguage(c);
                    });

                case AddPattern addPattern:
                    return UpdateAsync(addPattern, c =>
                    {
                        GuardAppPattern.CanAdd(Snapshot.Patterns, c);

                        AddPattern(c);
                    });

                case DeletePattern deletePattern:
                    return UpdateAsync(deletePattern, c =>
                    {
                        GuardAppPattern.CanDelete(Snapshot.Patterns, c);

                        DeletePattern(c);
                    });

                case UpdatePattern updatePattern:
                    return UpdateAsync(updatePattern, c =>
                    {
                        GuardAppPattern.CanUpdate(Snapshot.Patterns, c);

                        UpdatePattern(c);
                    });

                case ChangePlan changePlan:
                    return UpdateReturnAsync(changePlan, async c =>
                    {
                        GuardApp.CanChangePlan(c, Snapshot.Plan, appPlansProvider);

                        if (c.FromCallback)
                        {
                            ChangePlan(c);

                            return null;
                        }
                        else
                        {
                            var result = await appPlansBillingManager.ChangePlanAsync(c.Actor.Identifier, Snapshot.Id, Snapshot.Name, c.PlanId);

                            if (result is PlanChangedResult)
                            {
                                ChangePlan(c);
                            }

                            return result;
                        }
                    });

                default:
                    throw new NotSupportedException();
            }
        }

        public void Create(CreateApp command)
        {
            var appId = new NamedId<Guid>(command.AppId, command.Name);

            var events = new List<AppEvent>
            {
                CreateInitalEvent(command.Name),
                CreateInitialOwner(command.Actor),
                CreateInitialLanguage()
            };

            foreach (var pattern in initialPatterns)
            {
                events.Add(CreateInitialPattern(pattern.Key, pattern.Value));
            }

            foreach (var @event in events)
            {
                @event.Actor = command.Actor;
                @event.AppId = appId;

                RaiseEvent(@event);
            }
        }

        public void UpdateClient(UpdateClient command)
        {
            if (!string.IsNullOrWhiteSpace(command.Name))
            {
                RaiseEvent(SimpleMapper.Map(command, new AppClientRenamed()));
            }

            if (command.Permission.HasValue)
            {
                RaiseEvent(SimpleMapper.Map(command, new AppClientUpdated { Permission = command.Permission.Value }));
            }
        }

        public void UpdateLanguage(UpdateLanguage command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppLanguageUpdated()));
        }

        public void AssignContributor(AssignContributor command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppContributorAssigned()));
        }

        public void RemoveContributor(RemoveContributor command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppContributorRemoved()));
        }

        public void AttachClient(AttachClient command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppClientAttached()));
        }

        public void RevokeClient(RevokeClient command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppClientRevoked()));
        }

        public void AddLanguage(AddLanguage command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppLanguageAdded()));
        }

        public void RemoveLanguage(RemoveLanguage command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppLanguageRemoved()));
        }

        public void ChangePlan(ChangePlan command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppPlanChanged()));
        }

        public void AddPattern(AddPattern command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppPatternAdded()));
        }

        public void DeletePattern(DeletePattern command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppPatternDeleted()));
        }

        public void UpdatePattern(UpdatePattern command)
        {
            RaiseEvent(SimpleMapper.Map(command, new AppPatternUpdated()));
        }

        private void RaiseEvent(AppEvent @event)
        {
            if (@event.AppId == null)
            {
                @event.AppId = new NamedId<Guid>(Snapshot.Id, Snapshot.Name);
            }

            RaiseEvent(Envelope.Create(@event));
        }

        private static AppCreated CreateInitalEvent(string name)
        {
            return new AppCreated { Name = name };
        }

        private static AppPatternAdded CreateInitialPattern(Guid id, AppPattern pattern)
        {
            return new AppPatternAdded { PatternId = id, Name = pattern.Name, Pattern = pattern.Pattern, Message = pattern.Message };
        }

        private static AppLanguageAdded CreateInitialLanguage()
        {
            return new AppLanguageAdded { Language = Language.EN };
        }

        private static AppContributorAssigned CreateInitialOwner(RefToken actor)
        {
            return new AppContributorAssigned { ContributorId = actor.Identifier, Permission = AppContributorPermission.Owner };
        }

        public override void ApplyEvent(Envelope<IEvent> @event)
        {
            ApplySnapshot(Snapshot.Apply(@event));
        }
    }
}
