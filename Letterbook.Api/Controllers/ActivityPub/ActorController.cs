﻿using System.Text;
using ActivityPub.Types.AS;
using ActivityPub.Types.AS.Extended.Activity;
using ActivityPub.Types.Conversion;
using ActivityPub.Types.Util;
using AutoMapper;
using Letterbook.Adapter.ActivityPub;
using Letterbook.Adapter.ActivityPub.Mappers;
using Letterbook.Adapter.ActivityPub.Types;
using Letterbook.Api.Dto;
using Letterbook.Core;
using Letterbook.Core.Adapters;
using Letterbook.Core.Exceptions;
using Letterbook.Core.Extensions;
using Letterbook.Core.Models;
using Letterbook.Core.Values;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Letterbook.Api.Controllers.ActivityPub;

/// <summary>
/// Provides the endpoints specified for Actors in the ActivityPub spec
/// https://www.w3.org/TR/activitypub/#actors
/// </summary>
[ApiController]
[Route("[controller]")]
[Consumes("application/ld+json",
    "application/ld+json; profile=\"https://www.w3.org/ns/activitystreams\"", 
    "application/activity+json")]
// [JsonLdSerializer]
public class ActorController : ControllerBase
{
    private readonly ILogger<ActorController> _logger;
    private readonly IJsonLdSerializer _ldSerializer;
    private readonly IProfileService _profileService;
    private readonly IActivityMessageService _messageService;
    private static readonly IMapper ActorMapper = new Mapper(AsApMapper.ActorConfig);

    public ActorController(IOptions<CoreOptions> config, ILogger<ActorController> logger, IJsonLdSerializer ldSerializer,
        IProfileService profileService, IActivityMessageService messageService)
    {
        _logger = logger;
        _ldSerializer = ldSerializer;
        _profileService = profileService;
        _messageService = messageService;
        _logger.LogInformation("Loaded {Controller}", nameof(ActorController));
    }


    [HttpGet]
    [Route("{id}")]
    public async Task<IActionResult> GetActor(string id)
    {
        Guid localId;
        try
        {
            localId = ShortId.ToGuid(id);
        }
        catch (Exception)
        {
            return BadRequest();
        }

        var profile = await _profileService.LookupProfile(localId);
        if (profile == null) return NotFound();
        var actor = ActorMapper.Map<PersonActorExtension>(profile);

        return Ok(actor);
    }

    [HttpGet]
    [ActionName("Followers")]
    [Route("{id}/collections/[action]")]
    public IActionResult GetFollowers(int id)
    {
        throw new NotImplementedException();
    }

    [HttpGet]
    [ActionName("Following")]
    [Route("{id}/collections/[action]")]
    public IActionResult GetFollowing(int id)
    {
        throw new NotImplementedException();
    }

    [HttpGet]
    [ActionName("Liked")]
    [Route("{id}/collections/[action]")]
    public IActionResult GetLiked(int id)
    {
        throw new NotImplementedException();
    }

    [HttpGet]
    [ActionName("Inbox")]
    [Route("{id}/[action]")]
    public IActionResult GetInbox(int id)
    {
        throw new NotImplementedException();
    }

    [HttpPost]
    [ActionName("Inbox")]
    [Route("{id}/[action]")]
    [ProducesResponseType(typeof (AsAp.Activity), StatusCodes.Status200OK, "application/ld+json")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status421MisdirectedRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> PostInbox(string id, ASType activity)
    {
        var localId = ShortId.ToGuid(id);
        _logger.LogInformation("Start processing {Activity} activity", activity.GetType());
        _logger.LogDebug("Activity {Detail}", _ldSerializer.Serialize(activity));
        await LogActivity();
        try
        {
            if (activity.Is<AcceptActivity>(out var accept))
                return await InboxAccept(localId, accept);
            if (activity.Is<FollowActivity>(out var follow))
                return await InboxFollow(localId, follow);
            if (activity.Is<UndoActivity>(out var undo))
                return await InboxUndo(localId, undo);
            
            _logger.LogWarning("Ignored unknown activity {ActivityType}", activity.GetType());
            _logger.LogDebug("Ignored unknown activity details {@Activity}", activity);
            return Accepted();
        }
        catch (CoreException e)
        {
            if (e.Flagged(ErrorCodes.WrongAuthority)) return StatusCode(421, new ErrorMessage(e));
            if (e.Flagged(ErrorCodes.InvalidRequest)) return UnprocessableEntity(new ErrorMessage(e));
            if (e.Flagged(ErrorCodes.MissingData)) return NotFound(new ErrorMessage(e));
            if (e.Flagged(ErrorCodes.DuplicateEntry)) return Conflict(new ErrorMessage(e));
            throw;
        }
    }


    [HttpPost]
    [Route("[action]")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<ActionResult> SharedInbox(AsAp.Activity activity)
    {
        throw new NotImplementedException();
        // await _activityService.ReceiveNotes(new Note[] { }, Enum.Parse<ActivityType>(activity.Type), null);
        return Accepted();
    }

    [HttpGet]
    [ActionName("Outbox")]
    [Route("{id}/[action]")]
    public IActionResult GetOutbox(int id)
    {
        throw new NotImplementedException();
    }

    [HttpPost]
    [ActionName("Outbox")]
    [Route("{id}/[action]")]
    public IActionResult PostOutbox(int id)
    {
        throw new NotImplementedException();
    }
    
    /* * * * * * * * * * * * *
     * Support methods       *
     * * * * * * * * * * * * */

    private async ValueTask LogActivity()
    {
        if (!_logger.IsEnabled(LogLevel.Debug) || !HttpContext.Request.Body.CanRead) return;

        try
        {
            HttpContext.Request.Body.Seek(0, SeekOrigin.Begin);
            var buffer = new byte[HttpContext.Request.Body.Length];
            var read = await HttpContext.Request.Body.ReadAsync(buffer, 0, (int)HttpContext.Request.Body.Length);

            if (read > 0)
            {
                _logger.LogDebug("Raw activity {Json}", Encoding.UTF8.GetString(buffer));
            }
            else _logger.LogDebug("Couldn't (re)read the request body");
        }
        finally
        {
            HttpContext.Request.Body.Seek(0, SeekOrigin.Begin);
        }
    }
    
    private async Task<IActionResult> InboxAccept(Guid localId, ASActivity activity)
    {
        throw new NotImplementedException();
    }
    
    private async Task<IActionResult> InboxUndo(Guid id, ASActivity activity)
    {
        if (activity.Object.SingleOrDefault()?.Value is not ASActivity activityObject)
            return new BadRequestObjectResult(new ErrorMessage(ErrorCodes.UnknownSemantics, 
                "Object of an Undo must have exactly one value, which must be another Activity"));
        if (activityObject.Is<AnnounceActivity>(out var announceActivity))
            throw new NotImplementedException();
        if (activityObject.Is<BlockActivity>(out var blockActivity))
            throw new NotImplementedException();
        if (activityObject.Is<FollowActivity>(out var followActivity))
        {
            if ((followActivity.Actor.SingleOrDefault() ?? activityObject.Actor.SingleOrDefault()) is not { } actor)
                return new BadRequestObjectResult(new ErrorMessage(ErrorCodes.UnknownSemantics,
                    "Undo:Follow can only be performed by exactly one Actor at a time"));
            if (!actor.TryGetId(out var actorId))
                return new BadRequestObjectResult(new ErrorMessage(ErrorCodes.InvalidRequest,
                    "Actor ID is required to Undo:Follow"));
            await _profileService.RemoveFollower(id, actorId);
            return new OkResult();
        }
        if (activityObject.Is<LikeActivity>(out var likeActivity))
            throw new NotImplementedException();
        
        _logger.LogInformation("Ignored unknown Undo target {ActivityType}", activityObject.TypeMap.ASTypes);
        return new AcceptedResult();
    }

    private async Task<IActionResult> InboxFollow(Guid localId, ASActivity followRequest)
    {
        if (followRequest.Actor.Count > 1) return BadRequest(new ErrorMessage(ErrorCodes.None, "Only one Actor can follow at a time"));
        var actor = followRequest.Actor.First();
        if (!actor.TryGetId(out var actorId))
            return BadRequest(new ErrorMessage(ErrorCodes.None, "Actor ID is required for follower"));

        followRequest.TryGetId(out var activityId);
        var relation = await _profileService.ReceiveFollowRequest(localId, actorId, activityId);
                    
        var resultActivity = relation.State switch
        {
            FollowState.Accepted => Activities.BuildActivity(ActivityType.Accept, relation.Follows, followRequest),
            FollowState.Pending => Activities.BuildActivity(ActivityType.TentativeAccept, relation.Follows, followRequest),
            _ => Activities.BuildActivity(ActivityType.Reject, relation.Follows, followRequest)
        };

        // We should publish and implement a reply negotiation mechanism. The options are basically response or inbox.
        // Response would mean reply "in-band" via the http response.
        // Inbox would mean reply "out-of-band" via a new POST to the actor's inbox.
        // But, that doesn't exist, yet. The if(false) is so we don't forget.
        var acceptResponse = false;
        if (acceptResponse) return Ok(resultActivity);
        _messageService.Deliver(relation.Follower.Inbox, resultActivity, relation.Follows);
        return Accepted();
    }
}

/*** The problem: Letterbook.AP can't serialize/deserialize nested Activities
// Parsed
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "id": "http://mastodon.castle/users/user#follows/32/undo",
  "type": "Undo",
  "actor": "http://mastodon.castle/users/user",
  "object": {
    "id": "http://mastodon.castle/6dc45337-040b-43d2-8238-de9b7f377750",
    "type": "Follow"
  }
}
// Received
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "id": "http://mastodon.castle/users/user#follows/32/undo",
  "type": "Undo",
  "actor": "http://mastodon.castle/users/user",
  "object": {
    "id": "http://mastodon.castle/6dc45337-040b-43d2-8238-de9b7f377750",
    "type": "Follow",
    "actor": "http://mastodon.castle/users/user",
    "object": "https://host.castle/actor/ahwPS_-DYE2h9RlTo0p4jg"
  }
}

// Sent
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "type": "Accept",
  "actor": "https://host.castle/actor/ahwPS_-DYE2h9RlTo0p4jg",
  "object": {
    "id": "http://mastodon.castle/fbdfbd1c-489e-4edb-980f-0273a03c0992",
    "type": "Follow"
  }
}

// Tried to Send
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "type": "Accept",
  "actor": "https://host.castle/actor/ahwPS_-DYE2h9RlTo0p4jg",
  "object": {
    "id": "http://mastodon.castle/fbdfbd1c-489e-4edb-980f-0273a03c0992",
    "type": "Follow",
    "actor": "http://mastodon.castle/users/user"
  }
}
*/