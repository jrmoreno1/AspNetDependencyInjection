﻿using System;
using System.Threading;
using System.Web;
using System.Web.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using AspNetDependencyInjection.Internal;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;

namespace AspNetDependencyInjection
{
	/// <summary>Controls the lifespan of the configured <see cref="IServiceCollection"/>. This class implements <see cref="IRegisteredObject"/> to ensure the root <see cref="IServiceProvider"/> is disposed when the <see cref="HostingEnvironment"/> shuts down. Only 1 instance of this class can exist at a time in a single AppDomain.</summary>
	public class ApplicationDependencyInjection : IDisposable, IRegisteredObject
	{
		private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim( initialCount: 1, maxCount: 1 );

		private readonly IServiceCollection                  services;
		private readonly ServiceProvider                     rootServiceProvider;
		private readonly List<IDependencyInjectionClient>    clients = new List<IDependencyInjectionClient>();

		/// <summary>Exposes <see cref="ImmutableApplicationDependencyInjectionConfiguration"/>.</summary>
		public ImmutableApplicationDependencyInjectionConfiguration Configuration { get; }

		/// <summary>Indicates if this <see cref="ApplicationDependencyInjection"/> instance has already been disposed.</summary>
		/// <remarks>This property is not public because consumers using <see cref="ApplicationDependencyInjection"/> (or a subclass) correctly do not *need* to know about this property.</remarks>
		protected internal Boolean IsDisposed { get; private set; }

		/// <summary>Constructor. Does not call any virtual methods. Calls <see cref="ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(IServiceCollection)"/> after using <c>services.TryAdd</c> to add a minimal set of required services.</summary>
		protected internal ApplicationDependencyInjection( ApplicationDependencyInjectionConfiguration configuration, IServiceCollection services )
		{
			// Validate:

			if( configuration == null ) throw new ArgumentNullException(nameof(configuration));
			if( services == null ) throw new ArgumentNullException(nameof(services));

			//

			if( !_semaphore.Wait( millisecondsTimeout: 0 ) )
			{
				throw new InvalidOperationException( "Another " + nameof(ApplicationDependencyInjection) + " has already been created in this AppDomain without being disposed first (or the previous dispose attempt failed)." );
			}

			this.Configuration = configuration.ToImmutable();

			// Register necessary internal services:
			services.TryAddDefaultDependencyInjectionOverrideService();
			services.TryAddSingleton<IServiceProviderAccessor>( sp => new AspNetDependencyInjection.Services.DefaultServiceProviderAccessor( this.Configuration, sp ) );
			services.TryAddSingleton<ObjectFactoryCache>();

			// Initialize fields:

			this.services            = services ?? throw new ArgumentNullException(nameof(services));
			this.rootServiceProvider = services.BuildServiceProvider( validateScopes: true );

			this.ObjectFactoryCache = this.rootServiceProvider.GetRequiredService<ObjectFactoryCache>();

			//

			HostingEnvironment.RegisterObject( this );
			global::Microsoft.Web.Infrastructure.DynamicModuleHelper.DynamicModuleUtility.RegisterModule( typeof( HttpContextScopeHttpModule ) );
			// NOTE: It is not possible to un-register a HttpModule. That's nothing to do with `Microsoft.Web.Infrastructure` - the actual module registry in `System.Web.dll` can only be added to, not removed from.
		}

		/// <summary>Invokes all of the factory delegates, passing <c>this</c> as the parameter. Then passes the clients into <see cref="UseClients(IEnumerable{IDependencyInjectionClient})"/>.</summary>
		protected internal virtual void CreateClients( IEnumerable<Func<ApplicationDependencyInjection,IServiceProvider,IDependencyInjectionClient>> clientFactories )
		{
			if( clientFactories == null ) throw new ArgumentNullException(nameof(clientFactories));

			//

			IEnumerable<IDependencyInjectionClient> clients = clientFactories
				.Select( cf => cf( this, this.rootServiceProvider ) );

			this.UseClients( clients );
		}

		/// <summary>Copies all of the non-null <see cref="IDependencyInjectionClient"/> instances from <paramref name="clients"/> into the private clients list. The clients will be disposed inside <see cref="Dispose(bool)"/>.</summary>
		protected internal virtual void UseClients( IEnumerable<IDependencyInjectionClient> clients )
		{
			if( clients == null ) throw new ArgumentNullException(nameof(clients));

			//

			this.clients.AddRange( clients.Where( c => c != null ) );
		}

		/// <summary>Gets the root <see cref="IServiceProvider"/> instance. Throws <see cref="ObjectDisposedException"/> if this <see cref="ApplicationDependencyInjection"/> instance is already disposed. This property is not intended to be used to resolve services directly, but is intended for use by <see cref="IDependencyInjectionClient"/> classes so they can set-up custom scopes and resolvers.</summary>
		// NOTE: Alternate to exposing this as a property, it could be passed-in as a second parameter to the `IDependencyInjectionClient` factory methods. Hmmm.
		internal IServiceProvider RootServiceProvider
		{
			get
			{
				if( this.IsDisposed ) throw new ObjectDisposedException( objectName: this.GetType().FullName );

				return this.rootServiceProvider;
			}
		}

		/// <summary>Returns an instance of <see cref="ObjectFactoryCache"/> which caches service factories.</summary>
		public ObjectFactoryCache ObjectFactoryCache { get; }

#region Lifetime

		/// <summary>Calls <see cref="Dispose()"/>. This method is called by <see cref="HostingEnvironment"/>.</summary>
		/// <param name="immediate">This parameter is unused.</param>
		[System.Diagnostics.CodeAnalysis.SuppressMessage( "Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes", Justification = "The method calls Dispose, which is exposed to child types." )]
		void IRegisteredObject.Stop(Boolean immediate)
		{
			this.Dispose();
		}

		/// <summary>Consuming applications should not need to call this method directly as it is called by <see cref="HostingEnvironment"/>. This method calls <see cref="HostingEnvironment.UnregisterObject(IRegisteredObject)"/>, un-sets the <see cref="DependencyInjectionWebObjectActivator"/> from <see cref="HttpRuntime.WebObjectActivator"/> and restores its original value, and calls the <see cref="IDisposable.Dispose"/> method of the root <see cref="ServiceProvider"/>.</summary>
		public void Dispose()
		{
			this.Dispose( disposing: true );
			GC.SuppressFinalize( this ); // NOTE: It isn't necessary to call `SuppressFinalize` if the class doesn't have a finalizer.
		}

		/// <summary>See <see cref="IDisposable.Dispose"/>.</summary>
		/// <param name="disposing">When <c>true</c>, the <see cref="Dispose()"/> method was called. When <c>false</c> the finalizer was invoked.</param>
		protected virtual void Dispose( Boolean disposing )
		{
			if( this.IsDisposed ) return;

			if( disposing )
			{
				HostingEnvironment.UnregisterObject(this);

				IEnumerable<IDependencyInjectionClient> clientsList = this.clients;
				if( clientsList != null )
				{
					foreach( IDependencyInjectionClient client in clientsList )
					{
						client.Dispose();
					}
				}

				this.rootServiceProvider.Dispose();

				_semaphore.Release();
			}

			this.IsDisposed = true;
		}

#endregion

		/// <summary>See the documentation for <see cref="GetServiceProviderForCurrentHttpContext(HttpContextBase)"/>. The <paramref name="httpContext"/> can have a null reference, in which case the root-service provider will be returned.</summary>
		public IServiceProvider GetServiceProviderForCurrentHttpContext( HttpContext httpContext )
		{
			return this.GetServiceProviderForCurrentHttpContext( httpContext == null ? (HttpContextBase)null : new HttpContextWrapper( httpContext ) );
		}

		/// <summary>Gets the current <see cref="IServiceProvider"/> from <paramref name="httpContext"/>'s <see cref="HttpContextBase.Items"/> or <see cref="HttpContextBase.ApplicationInstance"/> as set by <see cref="HttpContextScopeHttpModule"/>. If <paramref name="httpContext"/> is <c>null</c> or if the <see cref="IServiceProvider"/> was not found, a reference to the root <see cref="IServiceProvider"/> is returned.</summary>
		public IServiceProvider GetServiceProviderForCurrentHttpContext( HttpContextBase httpContext )
		{
			if( httpContext != null )
			{
				if( this.Configuration.UseRequestScopes && httpContext.TryGetRequestServiceScope( out IServiceScope requestServiceScope ) ) // This will return false when `UseRequestScopes == false`.
				{
					return requestServiceScope.ServiceProvider;
				}
				else if( this.Configuration.UseHttpApplicationScopes && httpContext.ApplicationInstance.TryGetHttpApplicationServiceScope( out IServiceScope httpApplicationServiceScope ) ) // This will return false when `UseHttpApplicationScopes == true`.
				{
					return httpApplicationServiceScope.ServiceProvider;
				}
				else if( httpContext.ApplicationInstance.TryGetRootServiceProvider( out IServiceProvider httpApplicationRootServiceProvider ) ) // This should never return false
				{
					return httpApplicationRootServiceProvider;
				}
				else // This should never happen, but just-in-case:
				{
					return this.rootServiceProvider;
				}
			}
			else
			{
				return this.rootServiceProvider;
			}
		}
	}
}
