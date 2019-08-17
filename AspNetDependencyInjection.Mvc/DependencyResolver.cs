﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Mvc;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AspNetDependencyInjection.Internal
{
	/// <summary>Implements <see cref="IDependencyResolver"/> by using <see cref="DependencyInjectionWebObjectActivator"/>></summary>
	public class DependencyInjectionWebObjectActivatorDependencyResolver : IDependencyResolver // Surprisingly, IDependencyResolver does not implement IServiceProvider, weird.
	{
		private readonly DependencyInjectionWebObjectActivator webObjectActivator;

		public DependencyInjectionWebObjectActivatorDependencyResolver( DependencyInjectionWebObjectActivator webObjectActivator )
		{
			this.webObjectActivator = webObjectActivator ?? throw new ArgumentNullException(nameof(webObjectActivator));
		}

		/// <summary>Returns <c>null</c> if the requested service does not have a registered implementation.</summary>
		public Object GetService(Type serviceType)
		{
			if( serviceType == null ) throw new ArgumentNullException(nameof(serviceType));

			// System.Web.Mvc's default DependencyResolver just wraps Activator, with one key difference:
			// If the requested type is an interface or is abstract, then it simply returns null.
			// However, obviously we don't want to do that because *if* it does have an implementation it should be returned.
			
			/*
			if( serviceType.IsInterface || serviceType.IsAbstract )
			{
				return null;
			}
			else
			{
				return this.webObjectActivator.GetService( serviceType );
			}
			*/
			
			// `useFallback: false` so Activator won't be used for types under `System.Web.Mvc`.
			if( this.webObjectActivator.TryGetService( serviceType, useFallback: false, out Object service ) )
			{
				return service;
			}
			else
			{
				return null;
			}
		}

		// TODO: Is this a correct implementation of IDependencyResolver.GetServices?
		public IEnumerable<Object> GetServices(Type serviceType)
		{
			return this.webObjectActivator.GetServices( serviceType );
		}
	}

	// It isn't necessary to subclass DefaultControllerFactory or implement IControllerFactory.
	// Because DefaultControllerFactory uses the registered IDependencyResolver anyway.
}
