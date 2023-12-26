﻿using Bogus;
using Letterbook.Core.Models;

namespace Letterbook.Core.Tests.Fakes;

public sealed class FakeNote : Faker<Note>
{
    public FakeNote(Profile profile)
    {
        CustomInstantiator(faker => new Note(new Uri(faker.Internet.UrlWithPath())));
        
        RuleFor(note => note.Creators, _ => new HashSet<Profile>(){profile});
    }

    public FakeNote(FakeProfile profileFaker) : this(profileFaker.Generate())
    {}

    public FakeNote() : this(new FakeProfile().Generate())
    {}
}