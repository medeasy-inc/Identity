﻿using DataFilters;
using Identity.API.Routing;
using Identity.CQRS.Commands.Accounts;
using Identity.CQRS.Queries.Accounts;
using Identity.DTO;
using MedEasy.CQRS.Core.Commands;
using MedEasy.CQRS.Core.Commands.Results;
using MedEasy.CQRS.Core.Queries;
using MedEasy.DAL.Repositories;
using MedEasy.DTO;
using MedEasy.DTO.Search;
using MedEasy.RestObjects;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Optional;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static MedEasy.RestObjects.LinkRelation;
using static Microsoft.AspNetCore.Http.StatusCodes;

namespace Identity.API.Features.Accounts
{
    /// <summary>
    /// Handles <see cref="Account"/>s resources
    /// </summary>
    [Route("v{version:apiVersion}/[controller]")]
    [ApiController]
    [ApiVersion("1.0")]
    [Authorize]
    public class AccountsController
    {
        public static string EndpointName => nameof(AccountsController)
            .Replace(nameof(Controller), string.Empty);

        private readonly IUrlHelper _urlHelper;
        private readonly IOptionsSnapshot<IdentityApiOptions> _apiOptions;
        private readonly IMediator _mediator;

        public AccountsController(IUrlHelper urlHelper, IOptionsSnapshot<IdentityApiOptions> apiOptions, IMediator mediator)
        {
            _urlHelper = urlHelper;
            _apiOptions = apiOptions;
            _mediator = mediator;
        }

        /// <summary>
        /// Gets a subset of accounts resources
        /// </summary>
        /// <param name="paginationConfiguration">paging configuration</param>
        /// <param name="ct">Notification to abort request execution</param>
        /// <returns></returns>
        /// <response code="200">A collection of resource</response>
        /// <response code="400">page or pageSize is negative or zero</response>
        [HttpGet]
        [HttpHead]
        [ProducesResponseType(typeof(GenericPagedGetResponse<Browsable<AccountInfo>>), Status200OK)]
        [ProducesResponseType(Status400BadRequest)]
        public async Task<IActionResult> Get([BindRequired, FromQuery] PaginationConfiguration paginationConfiguration, CancellationToken ct = default)
        {
            IdentityApiOptions apiOptions = _apiOptions.Value;
            paginationConfiguration.PageSize = Math.Min(paginationConfiguration.PageSize, apiOptions.MaxPageSize);

            GetPageOfAccountsQuery query = new GetPageOfAccountsQuery(paginationConfiguration);

            Page<AccountInfo> page = await _mediator.Send(query, ct)
                .ConfigureAwait(false);

            GenericPagedGetResponse<Browsable<AccountInfo>> result = new GenericPagedGetResponse<Browsable<AccountInfo>>(
                page.Entries.Select(resource => new Browsable<AccountInfo>
                {
                    Resource = resource,
                    Links = new[]
                    {
                        new Link {}
                    }
                }),

                first: _urlHelper.Link(RouteNames.DefaultGetAllApi, new { controller = EndpointName, page = 1, paginationConfiguration.PageSize }),
                previous: paginationConfiguration.Page > 1 && page.Count > 1
                    ? _urlHelper.Link(RouteNames.DefaultGetAllApi, new { controller = EndpointName, page = paginationConfiguration.Page - 1, paginationConfiguration.PageSize })
                    : null,
                next: paginationConfiguration.Page < page.Count
                    ? _urlHelper.Link(RouteNames.DefaultGetAllApi, new { controller = EndpointName, page = paginationConfiguration.Page + 1, paginationConfiguration.PageSize })
                    : null,
                last: _urlHelper.Link(RouteNames.DefaultGetAllApi, new { controller = EndpointName, page = page.Count, paginationConfiguration.PageSize }),
                total: page.Total
            );

            return new OkObjectResult(result);
        }

        /// <summary>
        /// Delete the account with the specified <paramref name="id"/>.
        /// </summary>
        /// <param name="id">id of the resource to delete.</param>
        /// <param name="ct">Notifies to abort the request execution.</param>
        /// <returns></returns>
        /// <response code="204">The resource was successfully deleted.</response>
        /// <response code="404">Resource to delete was not found</response>
        /// <response code="409">Resource cannot be deleted</response>
        /// <response code="403"></response>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
        {
            DeleteAccountInfoByIdCommand cmd = new DeleteAccountInfoByIdCommand(id);
            DeleteCommandResult cmdResult = await _mediator.Send(cmd, ct)
                .ConfigureAwait(false);

            IActionResult actionResult;
            switch (cmdResult)
            {
                case DeleteCommandResult.Done:
                    actionResult = new NoContentResult();
                    break;
                case DeleteCommandResult.Failed_Unauthorized:
                    actionResult = new UnauthorizedResult();
                    break;
                case DeleteCommandResult.Failed_NotFound:
                    actionResult = new NotFoundResult();
                    break;
                case DeleteCommandResult.Failed_Conflict:
                    actionResult = new StatusCodeResult(Status409Conflict);
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unexpected <{cmdResult}> result");
            }
            return actionResult;
        }

        /// <summary>
        /// Delete the specified account
        /// </summary>
        /// <param name="id">id of the account to delete</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("{id}")]
        [HttpHead("{id}")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
        {
            Option<AccountInfo> optionalAccount = await _mediator.Send(new GetOneAccountByIdQuery(id), ct)
                .ConfigureAwait(false);

            return optionalAccount.Match(
                some: account =>
               {
                   IList<Link> links = new List<Link>
                   {
                        new Link { Relation = Self, Method = "GET", Href = _urlHelper.Link(RouteNames.DefaultGetOneByIdApi, new { controller = EndpointName, account.Id }) },
                        new Link { Relation = "delete",Method = "DELETE", Href = _urlHelper.Link(RouteNames.DefaultGetOneByIdApi, new { controller = EndpointName, account.Id }) }
                   };

                   if (account.TenantId.HasValue)
                   {
                       links.Add(new Link { Relation = "tenant", Method = "GET", Href = _urlHelper.Link(RouteNames.DefaultGetOneByIdApi, new { controller = EndpointName, id = account.TenantId }) });
                   }

                   Browsable<AccountInfo> browsableResource = new Browsable<AccountInfo>
                   {
                       Resource = account,
                       Links = links
                   };

                   return new OkObjectResult(browsableResource);
               },
                none: () => (IActionResult)new NotFoundResult()
            );
        }

        /// <summary>
        /// Partially update an account resource.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Use the <paramref name="changes"/> to declare all modifications to apply to the resource.
        /// Only the declared modifications will be applied to the resource.
        /// </para>
        /// <para>    // PATCH api/accounts/3594c436-8595-444d-9e6b-2686c4904725</para>
        /// <para>
        ///     [
        ///         {
        ///             "op": "update",
        ///             "path": "/Email",
        ///             "from": "string",
        ///             "value": "bruce@wayne-entreprise.com"
        ///       }
        ///     ]
        /// </para>
        /// <para>The set of changes to apply will be applied atomically. </para>
        /// 
        /// </remarks>
        /// <param name="id">id of the resource to update.</param>
        /// <param name="changes">set of changes to apply to the resource.</param>
        /// <param name="ct"></param>
        /// <param name="cancellationToken">Notifies lower layers about the request abortion</param>
        /// <response code="204">The resource was successfully patched.</response>
        /// <response code="400">Changes are not valid for the selected resource.</response>
        /// <response code="404">Resource to "PATCH" not found</response>
        /// <response code="409">One or more patch operations would result in a invalid state</response>
        [HttpPatch("{id}")]
        [ProducesResponseType(typeof(ProblemDetails), Status400BadRequest)]
        [ProducesResponseType(Status204NoContent)]
        [ProducesResponseType(Status409Conflict)]
        [ProducesResponseType(Status404NotFound)]
        public async Task<IActionResult> Patch(Guid id, [BindRequired, FromBody] JsonPatchDocument<AccountInfo> changes, CancellationToken ct = default)
        {
            PatchInfo<Guid, AccountInfo> data = new PatchInfo<Guid, AccountInfo>
            {
                Id = id,
                PatchDocument = changes
            };
            PatchCommand<Guid, AccountInfo> cmd = new PatchCommand<Guid, AccountInfo>(data);

            ModifyCommandResult cmdResult = await _mediator.Send(cmd, ct)
                .ConfigureAwait(false);

            IActionResult actionResult;
            switch (cmdResult)
            {
                case ModifyCommandResult.Done:
                    actionResult = new NoContentResult();
                    break;
                case ModifyCommandResult.Failed_Unauthorized:
                    actionResult = new UnauthorizedResult();

                    break;
                case ModifyCommandResult.Failed_NotFound:
                    actionResult = new NotFoundResult();
                    break;
                case ModifyCommandResult.Failed_Conflict:
                    actionResult = new StatusCodeResult(Status409Conflict);
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unexpected <{cmdResult}> patch result");
            }

            return actionResult;
        }

        /// <summary>
        /// Creates an account resource.
        /// </summary>
        /// <param name="newAccount">Data of the new account</param>
        /// <param name="ct"></param>
        /// <returns></returns>
        /// <response code="201">The resource was  created successfully.</response>
        /// <response code="400">Changes are not valid for the selected resource.</response>
        /// <response code="409">An account with the same <see cref="AccountInfo.Username"/> or <see cref="AccountInfo.Email"/> already exist</response>
        [HttpPost]
        [AllowAnonymous]
        [ProducesResponseType(typeof(Browsable<AccountInfo>), Status201Created)]
        public async Task<IActionResult> Post([FromBody] NewAccountInfo newAccount, CancellationToken ct = default)
        {
            CreateAccountInfoCommand cmd = new CreateAccountInfoCommand(newAccount);

            Option<AccountInfo, CreateCommandResult> optionalAccount = await _mediator.Send(cmd, ct)
                .ConfigureAwait(false);

            return optionalAccount.Match(
                some: account =>
                {
                    Browsable<AccountInfo> browsableResource = new Browsable<AccountInfo>
                    {
                        Resource = account,
                        Links = new[]
                        {
                            new Link { Relation = Self, Method = "GET", Href = _urlHelper.Link(RouteNames.DefaultGetOneByIdApi, new {controller = EndpointName, account.Id}) }
                        }
                    };

                    return new CreatedAtRouteResult(RouteNames.DefaultGetOneByIdApi, new { controller = EndpointName, account.Id }, browsableResource);
                },
                none: cmdError =>
                {
                    IActionResult actionResult;
                    switch (cmdError)
                    {
                        case CreateCommandResult.Failed_Conflict:
                            actionResult = new StatusCodeResult(Status409Conflict);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException($"Unexpected <{cmdError}> result when creating an account");
                    }
                    return actionResult;
                });
        }


        /// <summary>
        /// Search for accounts
        /// </summary>
        /// <param name="search"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        [HttpGet("/search")]
        [HttpHead("/search")]
        public async Task<IActionResult> Search([BindRequired, FromQuery] SearchAccountInfo search, CancellationToken ct = default)
        {
        
            search.PageSize = Math.Min(search.PageSize, _apiOptions.Value.MaxPageSize);
            IList<IFilter> filters = new List<IFilter>();

            if (!string.IsNullOrWhiteSpace(search.Name))
            {
                filters.Add($"{nameof(AccountInfo.Name)}={search.Name}".ToFilter<AccountInfo>());
            }
            if (!string.IsNullOrWhiteSpace(search.Email))
            {
                filters.Add($"{nameof(AccountInfo.Email)}={search.Email}".ToFilter<AccountInfo>());
            }

            SearchQueryInfo<SearchAccountInfoResult> searchQuery = new SearchQueryInfo<SearchAccountInfoResult>
            {
                Page = search.Page,
                PageSize = search.PageSize,
                Filter = filters.Skip(1).Any()
                    ? new CompositeFilter { Logic = FilterLogic.And, Filters = filters }
                    : filters.Single(),

                Sort = search.Sort?.ToSort<SearchAccountInfoResult>() ?? new Sort<SearchAccountInfoResult>(nameof(SearchAccountInfoResult.UpdatedDate), SortDirection.Descending)
            };

            Page<SearchAccountInfoResult> searchResult = await _mediator.Send(new SearchQuery<SearchAccountInfoResult>(searchQuery), ct)
                .ConfigureAwait(false);

            bool hasNextPage = search.Page < searchResult.Count;
            return new OkObjectResult(new GenericPagedGetResponse<Browsable<SearchAccountInfoResult>>(

                items: searchResult.Entries.Select(x => new Browsable<SearchAccountInfoResult>
                {
                    Resource = x,
                    Links = new[]
                    {
                        new Link { Relation = Self, Method = "GET" }
                    }
                }),
                first: _urlHelper.Link(RouteNames.DefaultSearchResourcesApi, new { page = 1, search.PageSize, search.Name, search.Email, search.Sort, search.UserName, controller = EndpointName }),
                next: hasNextPage
                    ? _urlHelper.Link(RouteNames.DefaultSearchResourcesApi, new { page = search.Page + 1, search.PageSize, search.Name, search.Email, search.Sort, search.UserName, controller = EndpointName })
                    : null,
                last: _urlHelper.Link(RouteNames.DefaultSearchResourcesApi, new { page = searchResult.Count, search.PageSize, search.Name, search.Email, search.Sort, search.UserName, controller = EndpointName }),
                total: searchResult.Total
            ));
        }
    }
}
