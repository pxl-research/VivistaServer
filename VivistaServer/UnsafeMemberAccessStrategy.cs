using System;
using Fluid;

public class UnsafeMemberAccessStrategy : IMemberAccessStrategy
{
	public MemberNameStrategy MemberNameStrategy { get; set; } = MemberNameStrategies.Default;
	private readonly MemberAccessStrategy baseMemberAccessStrategy = new MemberAccessStrategy();
	public bool IgnoreCasing { get; set; }

	public IMemberAccessor GetAccessor(Type type, string name)
	{
		var accessor = baseMemberAccessStrategy.GetAccessor(type, name);
		if (accessor != null)
		{
			return accessor;
		}

		baseMemberAccessStrategy.Register(type, name);
		return baseMemberAccessStrategy.GetAccessor(type, name);
	}
	public void Register(Type type, string name, IMemberAccessor getter)
	{
		baseMemberAccessStrategy.Register(type, name, getter);
	}
}