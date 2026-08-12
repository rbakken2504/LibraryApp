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
# Where UseHttpsRedirection should send an insecure request. Singular _PORT is read by the redirect
# middleware only; the plural _PORTS would tell Kestrel to *listen* on https, which needs a
# certificate this image does not have. Without this the middleware cannot pick a target and logs
# "Failed to determine the https port for redirect" on every request instead of doing its job.
ENV ASPNETCORE_HTTPS_PORT=443

EXPOSE 8080
CMD ["sh", "-c", "ASPNETCORE_HTTP_PORTS=${PORT:-8080} exec dotnet LibraryApp.dll"]
