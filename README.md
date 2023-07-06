# AWS DynamoDB Session State Provider

The **Amazon DynamoDB Session State Provider** allows ASP.NET applications store their sessions inside DynamoDB. This helps applications scale across multiple application servers while maintaining session state across the system.

If you are looking to cache session state in DynamoDB from an _ASP.NET Core_ application, try the [AWS .NET Distributed Cache Provider](https://github.com/awslabs/aws-dotnet-distributed-cache-provider) instead.

## Change Log

The change log for the can be found in the [CHANGELOG.md](https://github.com/aws/aws-dotnet-session-provider/blob/master/CHANGELOG.md) file.


## Usage Information

This project builds a ASP.NET Session State provider that stores session in a DynamoDB table. The session state provider can retrieved from [NuGet][nuget-package].

For more information on using the session manager, see the session manager section in the [AWS SDK for .NET Developer Guide][developer-guide].


## Links

* [AWS Session State Provider NuGet package][nuget-package]
* [AWS Session Provider Developer Guide][developer-guide]
* [AWS .NET Developer Blog][dotnet-blog]
* [AWS SDK for .NET GitHub Repository][github-awssdk]
* [AWS SDK for .NET SDK][sdk-website]


[developer-guide]: https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/dynamodb-session-net-sdk.html
[nuget-package]: http://www.nuget.org/packages/AWS.SessionProvider/
[github-awssdk]: https://github.com/aws/aws-sdk-net
[sdk-website]: http://aws.amazon.com/sdkfornet
[dotnet-blog]: http://blogs.aws.amazon.com/net/
