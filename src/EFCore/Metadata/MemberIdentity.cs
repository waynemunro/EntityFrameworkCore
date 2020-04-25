// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Diagnostics;
using System.Reflection;
using JetBrains.Annotations;

namespace Microsoft.EntityFrameworkCore.Metadata
{
    /// <summary>
    ///     Represents the identity of an entity type member, can be based on <see cref="MemberInfo"/> or just the name.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplay(),nq}")]
    public readonly struct MemberIdentity
    {
        private readonly object _nameOrMember;

        [DebuggerStepThrough]
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public MemberIdentity([NotNull] string name)
            : this((object)name)
        {
        }

        [DebuggerStepThrough]
        public MemberIdentity([NotNull] MemberInfo memberInfo)
            : this((object)memberInfo)
        {
        }

        [DebuggerStepThrough]
        private MemberIdentity([CanBeNull] object nameOrMember)
        {
            _nameOrMember = nameOrMember;
        }

        public bool IsNone() => _nameOrMember == null;

        public static readonly MemberIdentity None = new MemberIdentity((object)null);

        [DebuggerStepThrough]
        public static MemberIdentity Create([CanBeNull] string name)
            => name == null ? None : new MemberIdentity(name);

        [DebuggerStepThrough]
        public static MemberIdentity Create([CanBeNull] MemberInfo member)
            => member == null ? None : new MemberIdentity(member);

        public string Name
        {
            [DebuggerStepThrough] get => MemberInfo?.GetSimpleMemberName() ?? (string)_nameOrMember;
        }

        public MemberInfo MemberInfo
        {
            [DebuggerStepThrough] get => _nameOrMember as MemberInfo;
        }

        private string DebuggerDisplay()
            => Name ?? "NONE";
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    }
}
