# Multi-stage: the SDK image is ~800MB and is not needed to run anything, so only the published
# output crosses into the runtime image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project files alone and restore before copying source, so an edit to a .cs file reuses
# the cached restore layer instead of re-downloading every package.
COPY LibraryApp/LibraryApp.csproj LibraryApp/
RUN dotnet restore LibraryApp/LibraryApp.csproj

COPY LibraryApp/ LibraryApp/
RUN dotnet publish LibraryApp/LibraryApp.csproj -c Release -o /app/publish --no-restore

# The tests are not copied in at all: they are a build-time concern, run by CI or locally, and
# shipping them would drag xunit into the image for no reason.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Render injects PORT and expects the service to listen on it; locally there is no PORT, so 8080
# (the .NET container default) stands in. Shell form is what makes the substitution happen at
# container start rather than at build time, and exec hands PID 1 to dotnet so the platform's
# SIGTERM reaches the app and shutdown stays graceful.
# Tells the app that something in front of it terminates TLS, so it must not redirect to HTTPS
# itself. The platform's health checks arrive here as plain HTTP with no X-Forwarded-Proto, and
# answering those with a 307 gets the instance pulled from routing — the public URL then returns the
# edge's own 404 for every path. The edge already redirects http to https, so this loses nothing.
ENV BehindTlsProxy=true

EXPOSE 8080
CMD ["sh", "-c", "ASPNETCORE_HTTP_PORTS=${PORT:-8080} exec dotnet LibraryApp.dll"]
