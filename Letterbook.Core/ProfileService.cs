using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Letterbook.Core.Adapters;
using Letterbook.Core.Exceptions;
using Letterbook.Core.Extensions;
using Letterbook.Core.Models;
using Letterbook.Core.Values;
using Medo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Letterbook.Core;

public class ProfileService : IProfileService, IAuthzProfileService
{
	private ILogger<ProfileService> _logger;
	private readonly Instrumentation _instrumentation;
	private CoreOptions _coreConfig;
	private IDataAdapter _profiles;
	private IProfileEventPublisher _profileEvents;
	private readonly IActivityPubClient _client;
	private readonly IHostSigningKeyProvider _hostSigningKeyProvider;
	private readonly IActivityScheduler _activity;

	public ProfileService(ILogger<ProfileService> logger, IOptions<CoreOptions> options, Instrumentation instrumentation,
		IDataAdapter profiles, IProfileEventPublisher profileEvents, IActivityPubClient client,
		IHostSigningKeyProvider hostSigningKeyProvider, IActivityScheduler activity)
	{
		_logger = logger;
		_instrumentation = instrumentation;
		_coreConfig = options.Value;
		_profiles = profiles;
		_profileEvents = profileEvents;
		_client = client;
		_hostSigningKeyProvider = hostSigningKeyProvider;
		_activity = activity;
	}

	public Task<Profile> CreateProfile(Profile profile)
	{
		throw new NotImplementedException();
	}

	public async Task<Profile> CreateProfile(Uuid7 ownerId, string handle)
	{
		var account = await _profiles.LookupAccount(ownerId);
		if (account == null)
		{
			_logger.LogError("Failed to create a new profile because no account exists with ID {AccountId}", ownerId);
			throw CoreException.MissingData("Cannot attach new Profile to Account because Account could not be found", typeof(Account),
				ownerId);
		}

		if (await _profiles.AnyProfile(handle))
		{
			_logger.LogError("Cannot create a new profile because a profile already exists with handle {Handle}",
				handle);
			throw CoreException.Duplicate("Profile already exists", handle);
		}

		var profile = Profile.CreateIndividual(_coreConfig.BaseUri(), handle);
		_profiles.Add(profile);
		profile.OwnedBy = account;
		account.LinkedProfiles.Add(new ProfileClaims(account, profile, [ProfileClaim.Owner]));
		await _profiles.Commit();
		await _profileEvents.Created(profile);

		return profile;
	}

	public async Task<UpdateResponse<Profile>> UpdateDisplayName(ProfileId localId, string displayName)
	{
		// TODO(moderation): vulgarity filters
		var profile = await RequireProfile(localId);
		if (profile.DisplayName == displayName)
			return new UpdateResponse<Profile>
			{
				Original = profile
			};

		var original = profile.ShallowClone();
		profile.DisplayName = displayName;
		await _profiles.Commit();
		await _profileEvents.Updated(original: original, updated: profile);

		return new UpdateResponse<Profile>
		{
			Original = original,
			Updated = profile
		};
	}


	public async Task<UpdateResponse<Profile>> UpdateDescription(ProfileId localId, string description)
	{
		var profile = await RequireProfile(localId);
		if (profile.Description == description)
			return new UpdateResponse<Profile>
			{
				Original = profile
			};

		var original = profile.ShallowClone();
		profile.Description = description;
		await _profiles.Commit();
		await _profileEvents.Updated(original: original, updated: profile);

		return new UpdateResponse<Profile>
		{
			Original = original,
			Updated = profile
		};
	}

	public async Task<UpdateResponse<Profile>> InsertCustomField(ProfileId localId, int index, string key,
		string value)
	{
		var profile = await RequireProfile(localId);
		if (profile.CustomFields.Length >= _coreConfig.MaxCustomFields)
			throw CoreException.InvalidRequest("Cannot add any more custom fields");
		var original = profile.ShallowClone();

		var customFields = profile.CustomFields.ToList();
		customFields.Insert(index, new CustomField { Label = key, Value = value });
		profile.CustomFields = customFields.ToArray();

		await _profiles.Commit();
		await _profileEvents.Updated(original: original, updated: profile);

		return new UpdateResponse<Profile>
		{
			Original = original,
			Updated = profile
		};
	}

	public async Task<UpdateResponse<Profile>> RemoveCustomField(ProfileId localId, int index)
	{
		var profile = await RequireProfile(localId);
		if (index >= profile.CustomFields.Length)
			throw CoreException.InvalidRequest("Cannot remove custom field because it doesn't exist");
		var original = profile.ShallowClone();

		var customFields = profile.CustomFields.ToList();
		customFields.RemoveAt(index);
		profile.CustomFields = customFields.ToArray();

		await _profiles.Commit();
		await _profileEvents.Updated(original: original, updated: profile);

		return new UpdateResponse<Profile>
		{
			Original = original,
			Updated = profile
		};
	}

	public async Task<UpdateResponse<Profile>> UpdateCustomField(ProfileId localId, int index, string key,
		string value)
	{
		var profile = await RequireProfile(localId);
		if (index >= profile.CustomFields.Length)
			throw CoreException.InvalidRequest("Cannot update custom field because it doesn't exist");
		var field = profile.CustomFields[index];
		if (field.Label == key && field.Value == value)
			return new UpdateResponse<Profile>
			{
				Original = profile
			};
		var original = profile.ShallowClone();

		profile.CustomFields[index] = new CustomField { Label = key, Value = value };

		await _profiles.Commit();
		await _profileEvents.Updated(original: original, updated: profile);

		return new UpdateResponse<Profile>
		{
			Original = original,
			Updated = profile
		};
	}

	public Task<UpdateResponse<Profile>> UpdateProfile(Profile profile)
	{
		throw new NotImplementedException();
	}

	public async Task<Profile?> LookupProfile(ProfileId profileId, ProfileId? relatedProfile)
	{
		var query = _profiles.SingleProfile(profileId)
			.TagWithCallSite()
			.TagWith(nameof(LookupProfile));
		query = relatedProfile.HasValue ? _profiles.WithRelation(query, relatedProfile.Value) : query.Include(p => p.Keys);

		return await query.FirstOrDefaultAsync();
	}

	public async Task<Profile?> LookupProfile(Uri fediId, ProfileId? relatedProfile)
	{
		var query = _profiles.SingleProfile(fediId)
			.TagWithCallSite()
			.TagWith(nameof(LookupProfile));
		query = relatedProfile.HasValue ? _profiles.WithRelation(query, relatedProfile.Value) : query;

		return await query.FirstOrDefaultAsync();
	}

	public async Task<IEnumerable<Profile>> FindProfiles(string handle)
	{
		var results = _profiles.FindProfilesByHandle(handle);

		var profiles = new List<Profile>();
		await foreach (var profile in results)
		{
			profiles.Add(profile);
		}

		return profiles;
	}

	private async Task<FollowerRelation> Follow(Profile self, Profile target, bool subscribeOnly)
	{
		if (target.HasLocalAuthority(_coreConfig))
		{
			// TODO(moderation): Check for blocks
			// TODO(moderation): Check for requiresApproval
			var relation = self.Follow(target, FollowState.Accepted);

			await _profiles.Commit();
			return relation;
		}

		// TODO(moderation): Check for blocks
		// TODO(moderation): Check for requiresApproval
		await _activity.Follow(target.Inbox, target, self);
		self.Follow(target, FollowState.Pending);

		await _profiles.Commit();
		return new FollowerRelation(self, target, FollowState.Pending);
	}

	public async Task<FollowerRelation> Follow(ProfileId selfId, Uri targetId)
	{
		var self = await _profiles.SingleProfile(selfId).WithRelation(targetId).FirstOrDefaultAsync()
		           ?? throw CoreException.MissingData<Profile>(selfId);
		var target = await _profiles.SingleProfile(targetId)
			             .Include(profile => profile.Headlining)
			             .WithRelation(selfId)
			             .FirstOrDefaultAsync()
		             ?? await ResolveProfile(targetId, self)
		             ?? throw CoreException.MissingData<Profile>(targetId);

		return await Follow(self, target, false);
	}

	public async Task<FollowerRelation> Follow(ProfileId selfId, ProfileId targetId)
	{
		var target = await _profiles.SingleProfile(targetId)
			             .WithRelation(selfId)
			             .Include(profile => profile.Headlining)
			             .FirstOrDefaultAsync()
		             ?? throw CoreException.MissingData<Profile>(targetId);

		var self = await _profiles.SingleProfile(selfId)
			           .WithRelation(targetId)
			           .Include(profile => profile.Audiences.Where(audience => audience.Source!.Id == targetId))
			           .FirstOrDefaultAsync()
		           ?? throw CoreException.MissingData<Profile>(selfId);

		return await Follow(self, target, false);
	}

	public async Task<FollowerRelation> ReceiveFollowRequest(Uri targetId, Uri followerId, Uri? requestId)
	{
		using var span = _instrumentation.Span<ProfileService>();
		span?.AddTag("targetId", targetId);
		span?.AddTag("followerId", followerId);
		span?.AddTag("requestId", requestId);
		if (targetId.Authority != _coreConfig.BaseUri().Authority)
		{
			_logger.LogWarning("Profile {FollowerId} tried to follow {TargetId}, but this is not the origin server", followerId, targetId);
			throw CoreException.WrongAuthority($"Cannot follow Profile {targetId} because it has a different origin server", targetId);
		}

		var target = await ResolveProfile(targetId);
		var follower = await ResolveProfile(followerId);

		return await ReceiveFollowRequest(target, follower, requestId);
	}

	public async Task<FollowerRelation> ReceiveFollowRequest(ProfileId localId, Uri followerId, Uri? requestId)
	{
		using var span = _instrumentation.Span<ProfileService>();
		span?.AddTag("localId", localId);
		span?.AddTag("followerId", followerId);
		span?.AddTag("requestId", requestId);
		var target = await RequireProfile(localId, followerId);
		var follower = await ResolveProfile(followerId);

		return await ReceiveFollowRequest(target, follower, requestId);
	}

	private async Task<FollowerRelation> ReceiveFollowRequest(Profile target, Profile follower, Uri? requestId)
	{
		using var span = Activity.Current;
		span?.AddTag("targetProfile.headlining", string.Join(", ", target.Headlining.Select(a => a.FediId)));

		// Check for existing relationship, in case we get duplicate requests
		var relation = target.FollowersCollection.FirstOrDefault(r => r.Follower == follower);
		if (relation is null)
		{
			relation = follower.Follow(target, FollowState.Accepted);
			await _profiles.Commit();
		}

		var actor = target;
		var inbox = follower.Inbox;
		switch (relation.State)
		{
			case FollowState.Accepted:
				await _activity.AcceptFollower(inbox, follower, actor);
				return relation;
			case FollowState.None:
			case FollowState.Rejected:
				await _activity.RejectFollower(inbox, follower, actor);
				return relation;
			case FollowState.Pending:
			default:
				await _activity.PendingFollower(inbox, follower, actor);
				return relation;
		}
	}

	public async Task<FollowerRelation> ReceiveFollowReply(ProfileId selfId, Uri targetId, FollowState response)
	{
		var profile = await _profiles.LookupProfileWithRelation(selfId, targetId) ??
		              throw CoreException.MissingData($"Cannot update Profile {selfId} because it could not be found", typeof(Profile),
			              selfId);
		var relation = profile.FollowingCollection.FirstOrDefault(r => r.Follows.FediId == targetId) ?? throw CoreException.MissingData(
			$"Cannot update following relationship for {selfId} concerning {targetId} because it could not be found",
			typeof(FollowerRelation), targetId);
		switch (response)
		{
			case FollowState.Accepted:
			case FollowState.Pending:
				relation.State = response;
				await _profiles.Commit();
				return relation;
			case FollowState.None:
			case FollowState.Rejected:
			default:
				profile.Unfollow(relation.Follows);
				profile.LeaveAudience(relation.Follows);
				_profiles.Delete(relation);
				relation.State = response;
				await _profiles.Commit();
				return relation;
		}
	}

	private async Task<FollowerRelation> RemoveFollower(Profile self, FollowerRelation relation)
	{
		self.RemoveFollower(relation.Follower);
		if (relation.Follower.HasLocalAuthority(_coreConfig))
			relation.Follower.LeaveAudience(self);

		await _profiles.Commit();
		await _activity.RemoveFollower(relation.Follower.Inbox, relation.Follower, self);
		relation.State = FollowState.None;
		return relation;
	}

	public async Task<FollowerRelation> RemoveFollower(ProfileId selfId, Uri followerId)
	{
		var self = await _profiles.SingleProfile(selfId).WithRelation(followerId).FirstOrDefaultAsync()
		           ?? throw CoreException.MissingData<Profile>(selfId);
		var relation = self.FollowersCollection
			.FirstOrDefault(p => p.Follower.FediId.OriginalString == followerId.OriginalString);

		if (relation is null) return new FollowerRelation(Profile.CreateEmpty(followerId), self, FollowState.None);
		return await RemoveFollower(self, relation);
	}

	public async Task<FollowerRelation> RemoveFollower(ProfileId selfId, ProfileId followerId)
	{
		var self = await _profiles.SingleProfile(selfId)
			.WithRelation(followerId)
			.FirstOrDefaultAsync() ?? throw CoreException.MissingData<Profile>(selfId);
		var relation = self.FollowersCollection
			.FirstOrDefault(p => p.Follower.Id == followerId);

		if (relation is null) return new FollowerRelation(Profile.CreateEmpty(followerId), self, FollowState.None);
		return await RemoveFollower(self, relation);
	}

	private async Task<FollowerRelation> Unfollow(Profile self, FollowerRelation relation)
	{
		self.Unfollow(relation.Follows);
		if (self.Audiences.Count > 0)
		{
			foreach (var each in self.Audiences.Where(audience => audience.Source == relation.Follows))
			{
				self.Audiences.Remove(each);
			}
		}

		await _profiles.Commit();

		if (!relation.Follows.HasLocalAuthority(_coreConfig))
		{
			var target = relation.Follows;
			await _activity.Unfollow(target.Inbox, target, self);
		}

		relation.State = FollowState.None;
		return relation;
	}

	public async Task<FollowerRelation> Unfollow(ProfileId selfId, Uri targetId)
	{
		var self = await _profiles.WithRelation(_profiles.SingleProfile(selfId), targetId)
			           .Include(profile => profile.Audiences.Where(
				           audience => audience.Source != null && audience.Source.FediId.OriginalString == targetId.OriginalString))
			           .FirstOrDefaultAsync()
		           ?? throw CoreException.MissingData<Profile>(selfId);
		var relation = self.FollowingCollection
			.FirstOrDefault(p => p.Follows.FediId.OriginalString == targetId.OriginalString);

		if (relation is null) return new FollowerRelation(self, Profile.CreateEmpty(targetId), FollowState.None);
		return await Unfollow(self, relation);
	}

	public async Task<FollowerRelation?> Unfollow(ProfileId selfId, ProfileId targetId)
	{
		var self = await _profiles.WithRelation(_profiles.SingleProfile(selfId), targetId)
			           .Include(profile => profile.Audiences.Where(
				           audience => audience.Source != null && audience.Source.Id == targetId))
			           .FirstOrDefaultAsync()
		           ?? throw CoreException.MissingData<Profile>(selfId);
		var relation = self!.FollowingCollection
			.FirstOrDefault(p => p.Follows.Id == targetId);

		if (relation is null) return new FollowerRelation(self, Profile.CreateEmpty(targetId), FollowState.None);
		return await Unfollow(self, relation);
	}

	public Task<int> FollowerCount(Profile profile)
	{
		return _profiles.QueryFrom(profile, p => p.FollowersCollection)
			.CountAsync();
	}

	public Task<int> FollowingCount(Profile profile)
	{
		return _profiles.QueryFrom(profile, p => p.FollowingCollection)
			.CountAsync();
	}

	public Task ReportProfile(Uuid7 selfId, Uri profileId)
	{
		throw new NotImplementedException();
	}

	public Task ReportProfile(Uuid7 selfId, Uuid7 localId)
	{
		throw new NotImplementedException();
	}

	public async Task<IQueryable<Profile>> LookupFollowing(ProfileId profileId, DateTimeOffset? followedBefore, int limit)
	{
		var profile = await _profiles.LookupProfile(profileId)
		              ?? throw CoreException.MissingData<Profile>(profileId);
		return _profiles.QueryFrom(profile, query => query.FollowingCollection)
			.Take(Math.Max(limit, 200))
			.Include(relation => relation.Follows)
			.OrderByDescending(relation => relation.Date)
			.Where(relation => relation.Date < followedBefore)
			.Select(relation => relation.Follows);
	}

	public async Task<IQueryable<Profile>> LookupFollowers(ProfileId profileId, DateTimeOffset? followedBefore, int limit)
	{
		var profile = await _profiles.LookupProfile(profileId)
		              ?? throw CoreException.MissingData<Profile>(profileId);
		return _profiles.QueryFrom(profile, query => query.FollowersCollection)
			.Take(Math.Max(limit, 200))
			.Include(relation => relation.Follower)
			.OrderByDescending(relation => relation.Date)
			.Where(relation => relation.Date < followedBefore)
			.Select(relation => relation.Follower);
	}

	private async Task<FollowerRelation> AcceptFollower(Profile self, FollowerRelation relation)
	{
		if (relation.State != FollowState.Pending) return relation;

		relation.State = FollowState.Accepted;
		if (relation.Follower.HasLocalAuthority(_coreConfig))
			relation.Follower.Audiences.Add(Audience.Followers(self));
		// TODO: federate this
		await _profiles.Commit();
		return relation;
	}

	public async Task<FollowerRelation> AcceptFollower(ProfileId profileId, Uri followerId)
	{
		var self = await _profiles.WithRelation(_profiles.SingleProfile(profileId), followerId).FirstOrDefaultAsync()
		           ?? throw CoreException.MissingData<Profile>(profileId);
		var relation = self.FollowersCollection
			.FirstOrDefault(p => p.Follower.FediId.OriginalString == followerId.OriginalString);

		if (relation is null) return new FollowerRelation(Profile.CreateEmpty(followerId), self, FollowState.None);
		return await AcceptFollower(self, relation);
	}

	public async Task<FollowerRelation> AcceptFollower(ProfileId profileId, ProfileId followerId)
	{
		var self = await _profiles.WithRelation(_profiles.SingleProfile(profileId), followerId).FirstOrDefaultAsync()
		           ?? throw CoreException.MissingData<Profile>(profileId);
		var relation = self.FollowersCollection
			.FirstOrDefault(p => p.Follower.Id == followerId);

		if (relation is null) return new FollowerRelation(Profile.CreateEmpty(followerId), self, FollowState.None);
		return await AcceptFollower(self, relation);
	}

	[SuppressMessage("ReSharper", "ExplicitCallerInfoArgument")]
	private async Task<Profile> RequireProfile(ProfileId localId, Uri? relationId = null,
		[CallerMemberName] string name = "",
		[CallerFilePath] string path = "",
		[CallerLineNumber] int line = -1)
	{
		var query = _profiles.SingleProfile(localId)
			.Include(p => p.Headlining);

		var profile = relationId != null
			? await query.WithRelation(relationId).FirstOrDefaultAsync()
			: await query.Include(p => p.Keys).AsSplitQuery().FirstOrDefaultAsync();
		if (profile != null) return profile;

		_logger.LogError("Cannot update Profile {ProfileId} because it could not be found", localId);
		await _profiles.Cancel();
		throw CoreException.MissingData("Failed to update Profile because it could not be found", typeof(Profile), localId,
			null, name, path, line);
	}

	private async Task<Profile> ResolveProfile(Uri profileId,
		Profile? onBehalfOf = null,
		[CallerMemberName] string name = "",
		[CallerFilePath] string path = "",
		[CallerLineNumber] int line = -1)
	{
		var profile = await _profiles.SingleProfile(profileId)
			.Include(p => p.Headlining)
			.FirstOrDefaultAsync();

		if (profile != null
		    && !profile.HasLocalAuthority(_coreConfig)
		    && profile.Updated.Add(TimeSpan.FromHours(12)) >= DateTime.UtcNow) return profile;

		try
		{
			if (profile != null)
			{
				var fetched = await Fetch<Profile>(profileId, onBehalfOf);
				profile.ShallowCopy(fetched);
				_profiles.Update(profile);
			}
			else
			{
				profile = await Fetch<Profile>(profileId, onBehalfOf);
				_profiles.Add(profile);
			}

			await _profiles.Commit();
		}
		catch (AdapterException)
		{
			_logger.LogError("Cannot resolve Profile {ProfileId}", profileId);
			await _profiles.Cancel();
			throw;
		}

		_logger.LogInformation("Fetched Profile {ProfileId} from origin", profileId);
		return profile;
	}

	private async Task<TResult> Fetch<TResult>(Uri id, Profile? onBehalfOf) where TResult : IFederated
	{
		if (onBehalfOf != null)
		{
			return await _client.As(onBehalfOf).Fetch<TResult>(id);
		}

		Activity.Current?.AddEvent(new("HostKeySignature"));
		var key = await _hostSigningKeyProvider.GetSigningKey();
		return await _client.Fetch<TResult>(id, key);
	}

	public IAuthzProfileService As(IEnumerable<Claim> claims) => this;
}